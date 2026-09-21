using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;
using Zapret2UI.Services.Network;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Tor as the free way IN to Cloudflare: downloaded on demand, run as an ordinary child process, and
/// offered to the rest of the app as a SOCKS5 port on loopback.
///
/// <para><b>Why it is here at all.</b> MEASURED: Cloudflare picks the WARP exit country from the address
/// that dialled it, and a Tor exit in Germany gets a German WARP exit — even though Cloudflare tags the
/// Tor address itself as <c>T1</c> rather than a country. So Tor buys the country, and WARP buys the
/// reputation that Tor exits do not have. Neither half does the job alone: a Tor exit address is
/// refused by the very services this is for, and WARP dialled directly stays in the user's own
/// country.</para>
///
/// <para><b>Why it is downloaded rather than embedded.</b> The three files that matter are ~53 MB, the
/// application is already 187 MB, and a bundled tor.exe is treated far more harshly by antivirus
/// vendors than one the user asked for. The engine arrives the same way, so this is the shape the
/// project already has rather than a new one.</para>
///
/// <para><b>What is pinned and why.</b> One version, one sha256 of the whole archive. Verifying the
/// archive is stronger than verifying files after unpacking (nothing unpacked is ever trusted before
/// the hash matches), and archive.torproject.org keeps old releases, so a pin does not rot into a 404
/// the way <c>dist.torproject.org</c> does — measured: dist answers 404 for this very version while the
/// archive answers 200.</para>
/// </summary>
internal sealed class TorRuntime : IDisposable
{
    // ---- the pinned bundle -------------------------------------------------

    internal const string Version = "15.0.22";

    private const string ArchiveName = "tor-expert-bundle-windows-x86_64-" + Version + ".tar.gz";

    internal const string ArchiveUrl =
        "https://archive.torproject.org/tor-package-archive/torbrowser/" + Version + "/" + ArchiveName;

    /// <summary>sha256 of the archive itself. Checked against the file Tor's own signed
    /// <c>sha256sums-unsigned-build.txt</c> covers, verified once by hand with the Tor Browser
    /// developers' key before it was written down here.</summary>
    internal const string ArchiveSha256 = "231dad6b9cb401a54c260db7046965ef04e4f72ff071b140d423fb5da281ab1e";

    internal const long ArchiveSize = 22_432_027;

    /// <summary>What we keep out of the archive, and where it lands. The rest — conjure, tor-gencert,
    /// the docs — is left in the download: ~53 MB is already a lot to ask for.</summary>
    internal static IReadOnlyList<(string Entry, string Target)> Wanted => new[]
    {
        ("tor/tor.exe", AppPaths.TorExe),
        ("tor/pluggable_transports/lyrebird.exe", AppPaths.TorTransportExe),
        ("tor/pluggable_transports/pt_config.json", AppPaths.TorTransportConfig),
        ("data/geoip", AppPaths.TorGeoIpFile),
        ("data/geoip6", AppPaths.TorGeoIp6File),
    };

    /// <summary>Everything in the torrc is a path RELATIVE to the folder tor runs in. Verified rather
    /// than assumed: a Windows account whose profile has a space in it («C:\Users\John Doe\…») is
    /// ordinary, and tor's own parser for <c>ClientTransportPlugin</c> splits on spaces without
    /// honouring quotes — an absolute path there would break for those users and nobody else, which is
    /// the worst kind of bug to ship.</summary>
    private const string TorrcName = "torrc";

    // ---- state -------------------------------------------------------------

    private readonly object _lock = new();
    private Process? _proc;
    private TaskCompletionSource<bool>? _bootstrapped;
    private int _progress;

    /// <summary>When the percentage last moved, and whether the last attempt died of standing still —
    /// the difference between «мосты не работают» and «этой попытке не повезло».</summary>
    private long _lastProgress;
    private bool _stalled;

    /// <summary>The country and bridges the running tor was started with, so a second request for the
    /// same thing reuses it. The transport sweep asks once per attempt, and bootstrapping again for each
    /// of them would turn a few seconds into a few minutes.</summary>
    private string _options = "";

    /// <summary>Every line tor writes, for the journal.</summary>
    public event Action<string>? LogLine;

    /// <summary>Short lines meant for the screen: downloading, unpacking, bootstrapping.</summary>
    public event Action<string>? Status;

    /// <summary>The loopback port tor listens on for SOCKS5. 0 until it is up.</summary>
    public int SocksPort { get; private set; }

    /// <summary>The exit address the running tor is actually pinned to — empty with no pin, and empty
    /// again after a pin that carried nothing was dropped for any node in the country. Asked by whoever
    /// wants to open a second session: only a pin in force keeps the sessions on one address.</summary>
    internal string ActivePin { get; private set; } = "";

    public bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    /// <summary>True when the pinned bundle is unpacked and ready to run.</summary>
    internal static bool IsInstalled =>
        File.Exists(AppPaths.TorExe)
        && File.Exists(AppPaths.TorGeoIpFile)
        && File.Exists(AppPaths.TorTransportExe)
        && string.Equals(InstalledVersion, Version, StringComparison.OrdinalIgnoreCase);

    private static string InstalledVersion
    {
        get
        {
            try
            {
                return File.Exists(AppPaths.TorVersionFile)
                    ? File.ReadAllText(AppPaths.TorVersionFile).Trim()
                    : "";
            }
            catch { return ""; }
        }
    }

    // ---- getting it onto the disk ------------------------------------------

    /// <summary>Download, verify and unpack the bundle if it is not already there.
    ///
    /// <para>The hash is checked BEFORE anything is unpacked, and a mismatch deletes the download rather
    /// than keeping it for a retry: a file that is not the pinned bundle is not a file we want on the
    /// user's disk at all.</para>
    ///
    /// <para><paramref name="throughProxy"/> is a loopback SOCKS5 address to fetch through — in practice
    /// WARP itself. On the network this is written for, torproject.org is blocked outright, so a direct
    /// download fails with and without the bypass; through WARP it is an ordinary Cloudflare
    /// connection, and the name is resolved at the far end where nobody is filtering it.</para></summary>
    public async Task<WarpResult> EnsureReadyAsync(string throughProxy = "", CancellationToken ct = default)
    {
        if (IsInstalled) return WarpResult.Success("");

        AppPaths.EnsureCreated();
        string archive = Path.Combine(AppPaths.TempDir, ArchiveName);

        try
        {
            Report(throughProxy.Length > 0
                ? Loc.T("Загрузка Tor через WARP… {0:F1} МБ", ArchiveSize / 1_048_576.0)
                : Loc.T("Загрузка Tor… {0:F1} МБ", ArchiveSize / 1_048_576.0));
            using (HttpClient http = throughProxy.Length > 0
                       ? DohHttp.CreateThrough(throughProxy, TimeSpan.FromMinutes(15))
                       : DohHttp.Create(TimeSpan.FromMinutes(15)))
                await DownloadAsync(http, archive, ct).ConfigureAwait(false);

            Report(Loc.T("Проверка контрольной суммы Tor…"));
            string actual = await Task.Run(() => Sha256(archive), ct).ConfigureAwait(false);
            if (!string.Equals(actual, ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(archive);
                return WarpResult.Fail(Loc.T(
                    "Скачанный Tor не совпал с контрольной суммой — файл удалён, ничего не установлено. "
                    + "Повторите попытку позже."));
            }

            Report(Loc.T("Распаковка Tor…"));
            await Task.Run(() => Unpack(archive), ct).ConfigureAwait(false);
            File.WriteAllText(AppPaths.TorVersionFile, Version);

            Log(Loc.T("[tor] бандл {0} распакован в {1}", Version, AppPaths.TorDir));
            return WarpResult.Success("");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return WarpResult.Fail(throughProxy.Length > 0
                ? Loc.T("Не удалось скачать Tor даже через WARP: {0}", ex.Message)
                : Loc.T("Не удалось скачать Tor: {0}. Сайт Tor Project в России блокируют — включите "
                        + "обход на «Главной» и попробуйте снова.", ex.Message));
        }
        finally { TryDelete(archive); }
    }

    private async Task DownloadAsync(HttpClient http, string path, CancellationToken ct)
    {
        using var response = await http
            .GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? ArchiveSize;
        await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                                              1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        int lastShown = -1;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            int percent = total > 0 ? (int)(100 * done / total) : 0;
            if (percent >= lastShown + 5)
            {
                lastShown = percent;
                Report(Loc.T("Загрузка Tor… {0:F1}/{1:F1} МБ", done / 1_048_576.0, total / 1_048_576.0));
            }
        }
    }

    /// <summary>Pull the five files we need straight out of the .tar.gz — no shelling out, no temporary
    /// unpacked tree, and nothing executable written until its archive has matched the pin.</summary>
    private static void Unpack(string archive)
    {
        Directory.CreateDirectory(AppPaths.TorDir);
        Directory.CreateDirectory(AppPaths.TorTransportDir);
        Directory.CreateDirectory(AppPaths.TorDataDir);

        var wanted = Wanted.ToDictionary(w => w.Entry, w => w.Target, StringComparer.OrdinalIgnoreCase);

        using FileStream raw = File.OpenRead(archive);
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (tar.GetNextEntry() is { } entry)
        {
            string name = entry.Name.TrimStart('.', '/');
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (!wanted.TryGetValue(name, out string? target)) continue;

            string temporary = target + ".new";
            using (FileStream destination = File.Create(temporary))
                entry.DataStream?.CopyTo(destination);
            File.Move(temporary, target, overwrite: true);
            seen.Add(name);
        }

        string[] missing = wanted.Keys.Where(k => !seen.Contains(k)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                Loc.T("в архиве Tor не оказалось: {0}", string.Join(", ", missing)));
    }

    // ---- running it --------------------------------------------------------

    /// <summary>Start tor and wait until it says it is ready.
    ///
    /// <para>«Ready» is <c>Bootstrapped 100%</c> from tor's own log and nothing softer. A tor that is
    /// half-bootstrapped answers on its SOCKS port and then hangs, which downstream looks exactly like
    /// Cloudflare being unreachable — so the wait happens here, where the reason is still known.</para></summary>
    public async Task<WarpResult> StartAsync(string country, string bridgesText,
                                             string throughProxy = "", string exitAddress = "",
                                             CancellationToken ct = default)
    {
        string pin = ExitDirectory.SanitiseAddressList(exitAddress);
        string options = SanitiseCountry(country) + "\n" + pin + "\n" + (bridgesText ?? "").Trim();
        lock (_lock)
        {
            if (_proc is { HasExited: false } && string.Equals(_options, options, StringComparison.Ordinal))
                return WarpResult.Success("");
        }

        WarpResult ready = await EnsureReadyAsync(throughProxy, ct).ConfigureAwait(false);
        if (!ready.Ok) return ready;

        WarpResult started = await LaunchTwiceAsync(options, country, bridgesText, pin, ct)
                                   .ConfigureAwait(false);
        if (!started.Ok || pin.Length == 0) return started;

        // A pinned exit can reach «Bootstrapped 100%» and still carry nothing: bootstrapping never needs
        // an exit, and StrictNodes tells tor to DROP a stream it cannot route rather than quietly pick
        // another node. That is the failure this feature could not otherwise survive — the user would see
        // a connected Tor and a dead proxy — so the pin is proved with one real request, and a pin that
        // cannot carry it is dropped rather than kept.
        if (await CarriesTrafficAsync(ct).ConfigureAwait(false)) return started;

        Log(Loc.T("[tor] выбранный выход {0} не отвечает — дальше любой узел в стране", pin));
        Report(Loc.T("Выбранный выход Tor не отвечает — берётся любой в этой стране…"));
        return await LaunchTwiceAsync(SanitiseCountry(country) + "\n\n" + (bridgesText ?? "").Trim(),
                                      country, bridgesText, "", ct).ConfigureAwait(false);
    }

    /// <summary>One launch, and a second if the first simply stopped moving.
    ///
    /// <para>MEASURED: a bootstrap can stall — seen here at 60%, where it would have sat out the whole
    /// timeout, while starting over reached 100% in nineteen seconds. So a stall is retried ONCE, here,
    /// where it costs a second attempt rather than the caller's whole sweep.</para></summary>
    private async Task<WarpResult> LaunchTwiceAsync(string options, string country, string? bridgesText,
                                                    string pin, CancellationToken ct)
    {
        WarpResult first = await LaunchAsync(options, country, bridgesText, pin, ct).ConfigureAwait(false);
        if (first.Ok || !_stalled) return first;

        Log(Loc.T("[tor] подключение встало на месте — Tor перезапускается, ещё одна попытка"));
        return await LaunchAsync(options, country, bridgesText, pin, ct).ConfigureAwait(false);
    }

    /// <summary>Does a circuit actually carry a request? Asked of Cloudflare because the answer is tiny
    /// and the name is one the exit is certain to reach.</summary>
    private async Task<bool> CarriesTrafficAsync(CancellationToken ct)
    {
        try
        {
            using HttpClient http = Network.DohHttp.CreateThrough(
                "127.0.0.1:" + SocksPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.FromSeconds(30));
            string body = await http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct).ConfigureAwait(false);
            return body.Contains("ip=", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    /// <summary>One launch of tor and one wait for it to be ready.</summary>
    private async Task<WarpResult> LaunchAsync(string options, string country, string? bridgesText,
                                               string pin, CancellationToken ct)
    {
        Stop();

        List<string> bridges = ParseBridges(bridgesText);
        string ptConfig = ReadPtConfig();
        if (bridges.Count == 0) bridges = BuiltInBridges(ptConfig);

        List<string> transports = TransportLines(bridges, ptConfig);
        int port = FreeLoopbackPort();

        try
        {
            Directory.CreateDirectory(AppPaths.TorDataDir);
            File.WriteAllText(Path.Combine(AppPaths.TorDir, TorrcName),
                              BuildTorrc(port, country, bridges, transports, pin), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            return WarpResult.Fail(Loc.T("Не удалось записать настройки Tor: {0}", ex.Message));
        }

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.TorExe,
            WorkingDirectory = AppPaths.TorDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(TorrcName);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => Emit(e.Data);
        proc.ErrorDataReceived += (_, e) => Emit(e.Data);

        lock (_lock)
        {
            _bootstrapped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _progress = 0;
            _stalled = false;
        }
        Interlocked.Exchange(ref _lastProgress, Environment.TickCount64);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            proc.Dispose();
            return WarpResult.Fail(Loc.T("Не удалось запустить Tor: {0}", ex.Message));
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        lock (_lock)
        {
            _proc = proc;
            SocksPort = port;
        }

        Report(bridges.Count > 0
            ? Loc.T("Tor подключается через мосты…")
            : Loc.T("Tor подключается…"));

        var (ok, stalled) = await WaitForBootstrapAsync(TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (ok)
        {
            lock (_lock)
            {
                _options = options;
                ActivePin = pin;
            }
            Log(Loc.T("[tor] готов, SOCKS5 на 127.0.0.1:{0}", port));
            return WarpResult.Success("");
        }

        lock (_lock) _stalled = stalled;
        int reached = Interlocked.CompareExchange(ref _progress, 0, 0);
        Stop();
        return WarpResult.Fail(bridges.Count > 0
            ? Loc.T("Tor не подключился (дошёл до {0}%). Обычно это значит, что мосты не работают: "
                    + "нажмите «Получить мосты» или возьмите свежие у @GetBridgesBot в Telegram (команда "
                    + "/webtunnel) и включите обход на «Главной» — до самих мостов тоже надо дойти.", reached)
            : Loc.T("Tor не подключился (дошёл до {0}%). Без мостов из России он обычно и не подключается — "
                    + "добавьте мосты.", reached));
    }

    /// <summary>Stop tor. It owns no adapter and no routes, so this is only ever a process going
    /// away.</summary>
    public void Stop()
    {
        Process? proc;
        lock (_lock)
        {
            proc = _proc;
            _proc = null;
            _options = "";
            ActivePin = "";
            SocksPort = 0;
        }
        if (proc is null) return;

        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);       // takes lyrebird with it
                proc.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { /* already gone between the check and the kill */ }
        catch { /* best effort: a tor we cannot kill still owns nothing of ours */ }
        finally { proc.Dispose(); }
    }

    /// <summary>Kill a tor left behind by a crash — ours is the one running out of our own folder, and
    /// a copy somewhere else is the user's own Tor Browser and none of our business.</summary>
    internal static void DropStale()
    {
        foreach (string exe in new[] { AppPaths.TorExe, AppPaths.TorTransportExe })
        {
            try
            {
                if (!File.Exists(exe)) continue;
                foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                {
                    try
                    {
                        if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                            p.Kill(entireProcessTree: true);
                    }
                    catch { /* a process we may not inspect is not ours */ }
                    finally { p.Dispose(); }
                }
            }
            catch { /* never block startup on cleanup */ }
        }
    }

    public void Dispose() => Stop();

    // ---- the config --------------------------------------------------------

    /// <summary>The torrc, built from parts that were all checked first. Pure and internal so the test
    /// suite can read it back: this file decides which country the user comes out in, and a quietly
    /// dropped <c>ExitNodes</c> line would leave them coming out anywhere at all.</summary>
    internal static string BuildTorrc(int socksPort, string country,
                                      IReadOnlyList<string> bridges, IReadOnlyList<string> transports,
                                      string exitAddress = "")
    {
        var lines = new List<string>
        {
            "SocksPort 127.0.0.1:" + socksPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Relative to the working directory on purpose — see TorrcName.
            "DataDirectory data",
            "GeoIPFile geoip",
            "GeoIPv6File geoip6",
            "Log notice stdout",
            "AvoidDiskWrites 1",
            // Nothing but our own relay ever talks to this port, and it is bound to loopback anyway.
            "SocksPolicy accept 127.0.0.1",
        };

        // One address when the user has pinned one, the whole country otherwise. The address wins on
        // purpose: it already implies the country, and it is the thing that keeps the WARP exit still.
        string exits = ExitDirectory.ExitNodesValue(country, exitAddress);
        if (exits.Length > 0)
        {
            lines.Add("ExitNodes " + exits);
            // Without this, Tor treats ExitNodes as a preference and quietly leaves the country when it
            // cannot honour it — which is precisely the silent failure this feature cannot have.
            lines.Add("StrictNodes 1");
        }

        if (bridges.Count > 0)
        {
            lines.Add("UseBridges 1");
            lines.AddRange(transports);
            lines.AddRange(bridges.Select(b => "Bridge " + b));
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>Two ASCII letters or nothing. This string goes straight into the torrc between braces,
    /// so «anything else» is not a country and does not belong in a config file.</summary>
    internal static string SanitiseCountry(string country)
    {
        string code = country.Trim().Trim('{', '}').ToLowerInvariant();
        return code.Length == 2 && char.IsAsciiLetterLower(code[0]) && char.IsAsciiLetterLower(code[1])
            ? code
            : "";
    }

    /// <summary>How many bridge lines go into a torrc. A config with more is one nobody debugs, and tor
    /// tries them in order anyway — which is why the speed test puts the fastest on top.</summary>
    internal const int MaxBridges = 12;

    /// <summary>The bridge lines the user pasted, in the shape torrc wants them: without the leading
    /// «Bridge », without comments, and without anything that could not be one line of config.
    /// <paramref name="limit"/> is for torrc; callers that rewrite the user's box ask for every line.</summary>
    internal static List<string> ParseBridges(string? text, int limit = MaxBridges)
    {
        var lines = new List<string>();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim().Trim('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("bridge ", StringComparison.OrdinalIgnoreCase)) line = line[7..].Trim();
            if (line.Length is 0 or > 512) continue;
            if (line.Any(char.IsControl)) continue;
            if (!lines.Contains(line, StringComparer.OrdinalIgnoreCase)) lines.Add(line);
            if (lines.Count >= limit) break;
        }
        return lines;
    }

    /// <summary>The <c>ClientTransportPlugin</c> lines needed for the given bridges, taken from the
    /// bundle's own pt_config.json rather than written out here — the bundle is what knows which binary
    /// speaks which transport, and it has changed before.</summary>
    internal static List<string> TransportLines(IReadOnlyList<string> bridges, string ptConfigJson)
    {
        var kinds = bridges
            .Select(b => b.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "")
            .Where(k => k.Length > 0 && !k.Contains(':'))   // «1.2.3.4:443 …» is a plain relay, not a PT
            .Select(k => k.ToLowerInvariant())
            .ToHashSet();

        var result = new List<string>();
        if (kinds.Count == 0) return result;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ptConfigJson);
            if (!doc.RootElement.TryGetProperty("pluggableTransports", out JsonElement plugins)) return result;

            foreach (JsonProperty plugin in plugins.EnumerateObject())
            {
                string line = plugin.Value.GetString() ?? "";
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var speaks = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(t => t.ToLowerInvariant()).ToHashSet();
                if (!speaks.Overlaps(kinds)) continue;

                // Conjure ships a second binary we deliberately do not unpack, so a conjure bridge would
                // name a plugin that is not there — tor would refuse the whole config over it.
                if (line.Contains("conjure-client", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(line.Replace("${pt_path}", "pt/", StringComparison.Ordinal));
            }
        }
        catch { /* a pt_config we cannot read leaves the bridges to plain relays */ }

        return result;
    }

    /// <summary>The bundle's own bridges, used when the user has not pasted any. Measured: this bundle
    /// carries obfs4, snowflake and meek — and no WebTunnel, which is the one Russian users are usually
    /// handed, so «встроенные» is a fallback rather than the plan.</summary>
    internal static List<string> BuiltInBridges(string ptConfigJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(ptConfigJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("bridges", out JsonElement bridges)) return new List<string>();

            string preferred = root.TryGetProperty("recommendedDefault", out JsonElement rd)
                ? rd.GetString() ?? "obfs4"
                : "obfs4";

            if (!bridges.TryGetProperty(preferred, out JsonElement chosen)
                && !bridges.TryGetProperty("obfs4", out chosen))
                return new List<string>();

            return chosen.EnumerateArray()
                         .Select(b => b.GetString() ?? "")
                         .Where(b => b.Length > 0)
                         .Take(12)
                         .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string ReadPtConfig()
    {
        try { return File.ReadAllText(AppPaths.TorTransportConfig); }
        catch { return ""; }
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>Wait for <c>Bootstrapped 100%</c>, giving up early when tor stops making progress.
    ///
    /// <para>MEASURED: a bootstrap that stalls does so silently — here at «60% loading_descriptors»,
    /// where it sat until the whole timeout ran out, while a fresh start reached 100% in nineteen
    /// seconds. So the wait watches the PACE rather than only the clock: no movement for a while means
    /// this attempt is dead, and the caller can start over instead of the user watching a number that
    /// will not change.</para></summary>
    private async Task<(bool Ok, bool Stalled)> WaitForBootstrapAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task<bool>? wait;
        lock (_lock) wait = _bootstrapped?.Task;
        if (wait is null) return (false, false);

        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Task done = await Task.WhenAny(wait, Task.Delay(1000, ct)).ConfigureAwait(false);
            if (done == wait) return (wait.Result, false);

            if (Environment.TickCount64 - Interlocked.Read(ref _lastProgress) > StallMs)
                return (false, true);
        }
        return (false, false);
    }

    /// <summary>How long tor may sit on the same percentage before the attempt counts as dead.</summary>
    private const int StallMs = 75_000;

    private static readonly Regex BootstrapPercent =
        new(@"Bootstrapped (\d{1,3})%", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private void Emit(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string text = line.Trim();

        Match m = BootstrapPercent.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int percent))
        {
            int previous = Interlocked.Exchange(ref _progress, percent);
            if (percent > previous)
            {
                Interlocked.Exchange(ref _lastProgress, Environment.TickCount64);
                Report(Loc.T("Tor подключается… {0}%", percent));
            }
            if (percent >= 100) _bootstrapped?.TrySetResult(true);
        }

        Log("[tor] " + text);
    }

    private void Log(string line) => LogLine?.Invoke(line);

    private void Report(string line)
    {
        Status?.Invoke(line);
        Log("[tor] " + line);
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* a temp file is not worth failing over */ }
    }
}
