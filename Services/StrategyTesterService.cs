using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;

namespace ZapretUI.Services;

public enum TestKind { Tls, Http, Quic }

public sealed record StrategyTestResult(string Name, bool Works, long Ms, string Detail)
{
    public string Glyph => Works ? "✓" : "✗";
    public string MsText => Works && Ms > 0 ? $"{Ms} мс" : "";
}

/// <summary>
/// blockcheck-style auto-tester: for a target domain it launches a temporary
/// winws2 with each candidate strategy and checks whether the site becomes
/// reachable, reporting which strategies work.
///
/// The caller MUST stop the main engine first — only one winws2 should own the
/// WinDivert capture for a given port at a time.
/// </summary>
public sealed class StrategyTesterService
{
    public event Action<StrategyTestResult>? ResultReady;
    public event Action<string>? Status;

    private Process? _proc;

    public StrategyCandidate[] CandidatesFor(TestKind kind) => kind switch
    {
        TestKind.Http => StrategyCatalog.Http,
        TestKind.Quic => StrategyCatalog.Quic,
        _ => StrategyCatalog.Tls,
    };

    /// <summary>Run all candidates for a domain. Returns the full result list.</summary>
    public async Task<List<StrategyTestResult>> RunAsync(
        string domain, TestKind kind, CancellationToken ct)
    {
        var results = new List<StrategyTestResult>();
        domain = domain.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Не указан домен.");
        if (!File.Exists(AppPaths.WinwsExe))
            throw new FileNotFoundException("Движок не установлен.");

        // DNS sanity check.
        Status?.Invoke($"Резолвлю {domain}…");
        try { await Dns.GetHostAddressesAsync(domain, ct); }
        catch
        {
            throw new InvalidOperationException(
                $"Не удалось разрезолвить {domain}. Возможна DNS-блокировка — попробуйте другой DNS.");
        }

        // Baseline: does it work with NO bypass?
        Status?.Invoke("Базовая проверка (без обхода)…");
        var (baseOk, baseMs) = await ProbeAsync(domain, kind, ct);
        var baseline = new StrategyTestResult(
            "Без обхода (база)", baseOk, baseMs,
            baseOk ? "сайт открывается и так" : "заблокирован");
        results.Add(baseline);
        ResultReady?.Invoke(baseline);
        if (baseOk)
        {
            Status?.Invoke("Сайт доступен без обхода — стратегия не требуется.");
            return results;
        }

        var candidates = CandidatesFor(kind);
        for (int i = 0; i < candidates.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = candidates[i];
            Status?.Invoke($"[{i + 1}/{candidates.Length}] {c.Name}…");

            StrategyTestResult result;
            try
            {
                result = await TestCandidateAsync(domain, kind, c, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new StrategyTestResult(c.Name, false, 0, "ошибка: " + ex.Message);
            }
            results.Add(result);
            ResultReady?.Invoke(result);
        }

        int works = results.Count(r => r.Works && r != baseline);
        Status?.Invoke(works > 0
            ? $"Готово. Рабочих стратегий: {works}."
            : "Готово. Рабочих стратегий не найдено — попробуйте другой протокол/домен.");
        return results;
    }

    private async Task<StrategyTestResult> TestCandidateAsync(
        string domain, TestKind kind, StrategyCandidate c, CancellationToken ct)
    {
        StartEngine(BuildArgs(kind, c));
        try
        {
            // Give WinDivert time to load and attach.
            await Task.Delay(1300, ct);
            if (_proc is null || _proc.HasExited)
                return new StrategyTestResult(c.Name, false, 0, "движок не запустился (проверьте аргументы)");

            // Up to two attempts; success on the first wins.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var (ok, ms) = await ProbeAsync(domain, kind, ct);
                if (ok) return new StrategyTestResult(c.Name, true, ms, "работает");
            }
            return new StrategyTestResult(c.Name, false, 0, "не пробило");
        }
        finally
        {
            StopEngine();
            await Task.Delay(350, CancellationToken.None); // let the driver release
        }
    }

    private static List<string> BuildArgs(TestKind kind, StrategyCandidate c)
    {
        var a = new List<string>
        {
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-lib.lua"),
            "--lua-init=@" + Path.Combine(AppPaths.LuaDir, "zapret-antidpi.lua"),
        };
        switch (kind)
        {
            case TestKind.Http:
                a.Add("--wf-tcp-out=80");
                a.AddRange(new[] { "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req" });
                break;
            case TestKind.Quic:
                a.Add("--wf-udp-out=443");
                a.AddRange(new[] { "--filter-udp=443", "--filter-l7=quic", "--payload=quic_initial" });
                break;
            default:
                a.Add("--wf-tcp-out=443");
                a.AddRange(new[] { "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello" });
                break;
        }
        a.AddRange(c.Desync);
        return a;
    }

    /// <summary>Full winws2 args for a preset built from a winning candidate
    /// (capture + profile filters + desync, without the lua-init the engine adds).</summary>
    public static List<string> BuildPresetArgs(TestKind kind, StrategyCandidate c)
    {
        var a = BuildArgs(kind, c);
        a.RemoveRange(0, 2); // drop the two --lua-init lines; EngineService re-adds them
        return a;
    }

    private void StartEngine(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.WinwsExe,
            WorkingDirectory = AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var x in args) psi.ArgumentList.Add(x);

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _proc = p;
    }

    private void StopEngine()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(4000);
            }
        }
        catch { }
        finally
        {
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }
    }

    /// <summary>One connectivity attempt. Returns (success, milliseconds).</summary>
    private static async Task<(bool ok, long ms)> ProbeAsync(string domain, TestKind kind, CancellationToken ct)
    {
        string url = kind == TestKind.Http ? $"http://{domain}/" : $"https://{domain}/";

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.Zero,
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.None,
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (kind == TestKind.Quic)
        {
            req.Version = HttpVersion.Version30;
            req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(7));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            sw.Stop();
            // Any HTTP response (even 4xx/3xx) means the handshake/request got through DPI.
            return (true, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds);
        }
    }
}
