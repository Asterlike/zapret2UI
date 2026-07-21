using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zapret2UI.Models;

namespace Zapret2UI.Services;

/// <summary>
/// Silently keeps the zapret2 engine up to date from the official GitHub releases.
/// Downloads the release zip, verifies the Windows binaries against the release
/// sha256sum.txt, and installs only the files we actually need.
/// </summary>
public sealed class UpdaterService
{
    private const string EngineRepo = "bol-van/zapret2";
    private const string ReleasesLatestApi =
        "https://api.github.com/repos/bol-van/zapret2/releases/latest";

    /// <summary>This UI app's own releases (separate from the engine).</summary>
    private const string AppReleasesLatestApi =
        "https://api.github.com/repos/Asterlike/zapret2UI/releases/latest";
    private const string AppReleasesPage =
        "https://github.com/Asterlike/zapret2UI/releases/latest";

    private readonly HttpClient _http;
    private readonly DohResolver _doh = new();

    public UpdaterService()
    {
        // Resolve every hostname over DoH first (OS resolver as fallback). This is what makes the
        // download survive the common RU failure where github.com opens but the release asset host
        // *.githubusercontent.com does not: the ISP poisons that name in the system resolver, while
        // browsers (which do their own DoH) still reach it. On a healthy network DoH returns the same
        // IPs, so behaviour is unchanged. See DohConnectAsync.
        var handler = new SocketsHttpHandler { ConnectCallback = DohConnectAsync };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Zapret2UI", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Custom connect for <see cref="_http"/>: resolve the target host via DoH first, then the OS
    /// resolver, and TCP-connect to the first address that answers. TLS is still negotiated by the
    /// handler afterwards using the real hostname (SNI + cert validation unchanged), so this only
    /// bypasses a poisoned/blocked DNS answer — it does not weaken verification. Applies to every
    /// request the updater makes (API, page scrape, sha256 manifest, the zip download itself), so the
    /// whole chain — including the github.com→githubusercontent.com redirect — is DoH-resolved.
    /// </summary>
    private async ValueTask<Stream> DohConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        string host = ctx.DnsEndPoint.Host;
        int port = ctx.DnsEndPoint.Port;

        var candidates = new List<IPAddress>();
        if (IPAddress.TryParse(host, out var literal))
            candidates.Add(literal); // already an IP — no resolution needed
        else
        {
            try
            {
                foreach (var ip in await _doh.ResolveAsync(host, ct).ConfigureAwait(false))
                    if (IPAddress.TryParse(ip, out var addr)) candidates.Add(addr);
            }
            catch { /* DoH unavailable → OS resolver below */ }

            try
            {
                foreach (var addr in await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false))
                    if (!candidates.Contains(addr)) candidates.Add(addr);
            }
            catch { /* OS resolver failed too — any DoH candidates may still connect */ }
        }

        if (candidates.Count == 0)
            throw new IOException($"Не удалось определить адрес {host} (ни DoH, ни системный DNS не ответили).");

        Exception? last = null;
        foreach (var addr in candidates)
        {
            var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(addr, port, connectCts.Token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
            }
        }
        throw last ?? new IOException($"Не удалось подключиться к {host}.");
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

    // ---- app (this UI) self-update check ----------------------------------

    /// <summary>This app's own version (from the assembly), e.g. "0.1.0".</summary>
    public static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Latest app release (tag + page URL) from GitHub, or null on any failure.</summary>
    public async Task<(string Tag, string Url)?> FetchAppLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(AppReleasesLatestApi, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.TryGetProperty("html_url", out var u) ? (u.GetString() ?? "") : "";
            if (string.IsNullOrEmpty(url)) url = AppReleasesPage;
            return string.IsNullOrEmpty(tag) ? null : (tag, url);
        }
        catch { return null; }
    }

    /// <summary>Pull the numeric version out of an arbitrary release tag — handles any prefix
    /// ("v1.2.0", "Zapret2UI-0.3.0", "release-1.2"), not just a leading "v".</summary>
    public static Version? ParseTagVersion(string? tag)
    {
        var m = Regex.Match(tag ?? "", @"\d+(?:\.\d+){1,3}");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>True if the release tag is a newer SemVer than the running app.</summary>
    public static bool IsAppUpdate(string tag)
    {
        var latest = ParseTagVersion(tag);
        return latest is not null && Version.TryParse(AppVersion, out var cur) && latest > cur;
    }

    /// <summary>
    /// True if the installed engine is missing parts that newer UI versions need
    /// (e.g. the windivert filter set added after the first install). Such installs
    /// should be re-extracted even when the version tag is unchanged.
    /// </summary>
    public bool IsEngineComplete =>
        IsEngineInstalled && Directory.Exists(AppPaths.WinDivertFilterDir);

    /// <summary>
    /// Resolve the latest release and its asset URLs. Tries the GitHub <b>API</b> first, then falls
    /// back to scraping the regular <b>github.com</b> release page — some ISPs block api.github.com
    /// but allow github.com (or the reverse), so we try both before giving up.
    /// </summary>
    public async Task<ReleaseInfo> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            return await FetchLatestViaApiAsync(ct).ConfigureAwait(false);
        }
        catch (Exception apiEx) when (apiEx is not OperationCanceledException)
        {
            try { return await FetchLatestViaWebAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { throw apiEx; } // both paths failed — surface the original API error
        }
    }

    /// <summary>Latest release via api.github.com (JSON).</summary>
    private async Task<ReleaseInfo> FetchLatestViaApiAsync(CancellationToken ct)
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

    /// <summary>Latest release by scraping github.com (no API): the latest-redirect gives the tag,
    /// the expanded_assets partial gives the download links. Asset size is unknown (0).</summary>
    private async Task<ReleaseInfo> FetchLatestViaWebAsync(CancellationToken ct)
    {
        // 1. The tag — github.com/<repo>/releases/latest 302-redirects to …/releases/tag/<tag>.
        using var resp = await _http.GetAsync(
            $"https://github.com/{EngineRepo}/releases/latest", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        string finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        int i = finalUrl.IndexOf("/tag/", StringComparison.Ordinal);
        if (i < 0) throw new InvalidOperationException("Не удалось определить версию движка на github.com.");
        string tag = finalUrl[(i + 5)..].Trim('/');
        if (tag.Length == 0) throw new InvalidOperationException("Пустой тег релиза на github.com.");

        // 2. The assets — the expanded_assets partial lists every download link.
        string html = await _http.GetStringAsync(
            $"https://github.com/{EngineRepo}/releases/expanded_assets/{Uri.EscapeDataString(tag)}", ct)
            .ConfigureAwait(false);

        string? zipUrl = null, shaUrl = null;
        foreach (Match m in Regex.Matches(html,
            "href=\"(/" + Regex.Escape(EngineRepo) + "/releases/download/[^\"]+)\""))
        {
            string path = m.Groups[1].Value;
            string url = "https://github.com" + path;
            string name = path[(path.LastIndexOf('/') + 1)..];
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("openwrt", StringComparison.OrdinalIgnoreCase))
                zipUrl = url;
            else if (name.Equals("sha256sum.txt", StringComparison.OrdinalIgnoreCase))
                shaUrl = url;
        }

        if (zipUrl is null)
            throw new InvalidOperationException($"На странице релиза {tag} не найден zip-ассет.");

        return new ReleaseInfo(tag, zipUrl, shaUrl, 0);
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
        int verified = 0;
        bool winwsVerified = false;
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
            verified++;
            if (name.Equals("winws2.exe", StringComparison.OrdinalIgnoreCase)) winwsVerified = true;
        }

        // Fail closed: a manifest was provided (caller only calls us when hashes.Count > 0), so it must
        // actually cover what we staged. Zero matches = the manifest doesn't line up with the release →
        // we verified nothing. And if the manifest lists winws2.exe, that match is mandatory — otherwise
        // a tampered engine binary could install just because its name wasn't a manifest key.
        bool manifestHasWinws = hashes.Keys.Any(k => k.EndsWith("winws2.exe", StringComparison.OrdinalIgnoreCase));
        if (verified == 0 || (manifestHasWinws && !winwsVerified))
            throw new InvalidOperationException(
                "Не удалось проверить целостность движка по манифесту sha256 — установка отменена.");
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
