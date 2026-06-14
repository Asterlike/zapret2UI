using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Silently keeps the zapret2 engine up to date from the official GitHub releases.
/// Downloads the release zip, verifies the Windows binaries against the release
/// sha256sum.txt, and installs only the files we actually need.
/// </summary>
public sealed class UpdaterService
{
    private const string ReleasesLatestApi =
        "https://api.github.com/repos/bol-van/zapret2/releases/latest";

    private readonly HttpClient _http;

    public UpdaterService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZapretUI", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Currently installed engine tag, or null if the engine is absent.</summary>
    public string? InstalledVersion
    {
        get
        {
            try
            {
                if (File.Exists(AppPaths.WinwsExe) && File.Exists(AppPaths.EngineVersionFile))
                    return File.ReadAllText(AppPaths.EngineVersionFile).Trim();
            }
            catch { /* treat as not installed */ }
            return null;
        }
    }

    public bool IsEngineInstalled => File.Exists(AppPaths.WinwsExe);

    /// <summary>
    /// True if the installed engine is missing parts that newer UI versions need
    /// (e.g. the windivert filter set added after the first install). Such installs
    /// should be re-extracted even when the version tag is unchanged.
    /// </summary>
    public bool IsEngineComplete =>
        IsEngineInstalled && Directory.Exists(AppPaths.WinDivertFilterDir);

    /// <summary>Resolve the latest release and its relevant asset URLs.</summary>
    public async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(ReleasesLatestApi, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidOperationException("В ответе GitHub нет tag_name.");

        string? zipUrl = null, shaUrl = null;
        long zipSize = 0;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";

            // The all-platforms bundle, e.g. zapret2-v1.0.1.zip
            // (exclude the openwrt-embedded variant which is .tar.gz anyway)
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("openwrt", StringComparison.OrdinalIgnoreCase))
            {
                zipUrl = url;
                zipSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (name.Equals("sha256sum.txt", StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = url;
            }
        }

        if (zipUrl is null)
            throw new InvalidOperationException($"В релизе {tag} не найден zip-ассет.");

        return new ReleaseInfo(tag, zipUrl, shaUrl, zipSize);
    }

    /// <summary>True if a newer release than the installed one is available.</summary>
    public bool IsUpdateAvailable(ReleaseInfo latest) =>
        !string.Equals(InstalledVersion, latest.Tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>Download, verify and install the engine from the given release.</summary>
    public async Task InstallAsync(
        ReleaseInfo release,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        AppPaths.EnsureCreated();

        string zipPath = Path.Combine(AppPaths.TempDir, $"zapret2-{release.Tag}.zip");
        string stageDir = Path.Combine(AppPaths.TempDir, $"stage-{Guid.NewGuid():N}");

        try
        {
            // 1. Download the zip with progress.
            progress?.Report(new UpdateProgress(UpdatePhase.Downloading, 0, "Загрузка движка…"));
            await DownloadFileAsync(release.ZipUrl, zipPath, release.ZipSize, progress, ct)
                .ConfigureAwait(false);

            // 2. Pull the checksum manifest (per-binary hashes).
            Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
            if (release.Sha256Url is not null)
            {
                progress?.Report(new UpdateProgress(UpdatePhase.Verifying, 0, "Проверка контрольных сумм…"));
                string shaText = await _http.GetStringAsync(release.Sha256Url, ct).ConfigureAwait(false);
                hashes = ParseSha256Sum(shaText);
            }

            // 3. Extract only what we need into a staging folder.
            progress?.Report(new UpdateProgress(UpdatePhase.Extracting, 0, "Распаковка…"));
            Directory.CreateDirectory(stageDir);
            ExtractNeeded(zipPath, stageDir, ct);

            // 4. Verify the Windows binaries against the manifest (integrity).
            if (hashes.Count > 0)
                VerifyBinaries(stageDir, hashes);

            // 5. Move staged engine into place.
            progress?.Report(new UpdateProgress(UpdatePhase.Extracting, 0.95, "Установка…"));
            InstallStaged(stageDir);
            File.WriteAllText(AppPaths.EngineVersionFile, release.Tag);

            progress?.Report(new UpdateProgress(UpdatePhase.Done, 1.0, $"Готово — {release.Tag}"));
        }
        finally
        {
            TryDelete(zipPath);
            TryDeleteDir(stageDir);
        }
    }

    // ---- internals ---------------------------------------------------------

    private async Task DownloadFileAsync(
        string url, string destPath, long knownSize,
        IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? knownSize;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0)
            {
                double frac = Math.Clamp((double)read / total, 0, 1);
                progress?.Report(new UpdateProgress(
                    UpdatePhase.Downloading, frac,
                    $"Загрузка движка… {read / 1_048_576.0:F1}/{total / 1_048_576.0:F1} МБ"));
            }
        }
    }

    /// <summary>Parse <c>&lt;hash&gt;␠␠&lt;path&gt;</c> lines, keyed by file name.</summary>
    private static Dictionary<string, string> ParseSha256Sum(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 66) continue;
            int sp = line.IndexOf(' ');
            if (sp != 64) continue;
            string hash = line[..64];
            string path = line[sp..].TrimStart(' ', '*').Replace('\\', '/');
            // Key by "<arch>/<filename>" suffix so we can match staged files unambiguously.
            string fileName = path[(path.LastIndexOf('/') + 1)..];
            // Prefer the most specific key: arch/file, but also store bare file name.
            int binIdx = path.IndexOf("binaries/", StringComparison.OrdinalIgnoreCase);
            if (binIdx >= 0)
            {
                string rel = path[(binIdx + "binaries/".Length)..]; // e.g. windows-x86_64/winws2.exe
                map[rel] = hash;
            }
            map[fileName] = hash;
        }
        return map;
    }

    private static void ExtractNeeded(string zipPath, string stageDir, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // top folder inside the zip, e.g. "zapret2-v1.0.1/"
        string? top = null;
        foreach (var e in zip.Entries)
        {
            int slash = e.FullName.IndexOf('/');
            if (slash > 0) { top = e.FullName[..(slash + 1)]; break; }
        }
        if (top is null) throw new InvalidOperationException("Неожиданная структура архива релиза.");

        string arch = AppPaths.ReleaseArchFolder;
        string binPrefix = $"{top}binaries/{arch}/";
        string luaPrefix = $"{top}lua/";
        string filesPrefix = $"{top}files/";
        string wfPrefix = $"{top}init.d/windivert.filter.examples/";

        bool gotWinws = false;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue; // directory marker

            string? target = null;
            if (entry.FullName.StartsWith(binPrefix, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, entry.FullName[binPrefix.Length..]);
            else if (entry.FullName.StartsWith(luaPrefix, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, "lua", entry.FullName[luaPrefix.Length..]);
            else if (entry.FullName.StartsWith(filesPrefix, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, "files", entry.FullName[filesPrefix.Length..]);
            else if (entry.FullName.StartsWith(wfPrefix, StringComparison.OrdinalIgnoreCase) &&
                     entry.Name.StartsWith("windivert_part", StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(stageDir, "windivert.filter", entry.FullName[wfPrefix.Length..]);

            if (target is null) continue;

            // Defense in depth against zip path traversal.
            string fullStage = Path.GetFullPath(stageDir) + Path.DirectorySeparatorChar;
            string fullTarget = Path.GetFullPath(target);
            if (!fullTarget.StartsWith(fullStage, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Подозрительный путь в архиве: {entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
            entry.ExtractToFile(fullTarget, overwrite: true);

            if (entry.Name.Equals("winws2.exe", StringComparison.OrdinalIgnoreCase))
                gotWinws = true;
        }

        if (!gotWinws)
            throw new InvalidOperationException(
                $"В архиве нет winws2.exe для {arch}. Возможно, релиз без Windows-бинарников.");
    }

    private static void VerifyBinaries(string stageDir, Dictionary<string, string> hashes)
    {
        string arch = AppPaths.ReleaseArchFolder;
        // Windows binaries were staged flat at the root of stageDir.
        foreach (var file in Directory.EnumerateFiles(stageDir, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            string archKey = $"{arch}/{name}";
            string? expected = hashes.GetValueOrDefault(archKey) ?? hashes.GetValueOrDefault(name);
            if (expected is null) continue; // not all files are listed (only binaries are)

            string actual = ComputeSha256(file);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Контрольная сумма не совпала для {name}. Загрузка повреждена или подменена.");
        }
    }

    private static void InstallStaged(string stageDir)
    {
        foreach (var src in Directory.EnumerateFiles(stageDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(stageDir, src);
            string dst = Path.Combine(AppPaths.EngineDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
