using System.Diagnostics;
using System.IO;
using System.Text;
using Zapret2UI.Models;

namespace Zapret2UI.Services;

/// <summary>Per-endpoint outcome of a candidate (TLS 1.2 / 1.3 + full HTTPS GET).</summary>
public sealed record AutoHostResult(string Host, DiagStatus Tls12, DiagStatus Tls13, DiagStatus Https);

/// <summary>Score of one candidate against the goal endpoints (lower Fail = better).</summary>
public sealed record AutoScore(
    string Name, int Ok, int Fail, int Total,
    ComboStrategy? Strategy = null, IReadOnlyList<AutoHostResult>? Hosts = null)
{
    public string Detail => Fail == 0 ? $"всё прошло ({Ok}/{Total})" : $"{Ok}/{Total} прошло, ошибок: {Fail}";
    public string Glyph => Fail == 0 ? "✓" : (Ok > 0 ? "≈" : "✗");
    public double Ratio => Total > 0 ? (double)Ok / Total : 0;
    public IReadOnlyList<AutoHostResult> HostList => Hosts ?? Array.Empty<AutoHostResult>();
    public bool CanApply => Strategy is not null;
}

/// <summary>
/// Auto-selector: launches each <see cref="ComboStrategyCatalog"/> candidate, probes
/// the goal endpoints (TLS 1.2 + 1.3 of each), scores it by how many succeed, and
/// returns the best one — so a single strategy is chosen that maximises what works
/// across YouTube and Discord together (or just one, per the chosen scope).
///
/// The caller MUST stop the main engine first.
/// </summary>
public sealed class AutoSelectService : IDisposable
{
    public event Action<string>? Status;
    public event Action<AutoScore>? ScoreReady;
    /// <summary>Fired with the candidate name right before it starts being probed.</summary>
    public event Action<string>? CandidateStarted;
    /// <summary>Fired after each goal host is probed (host, TLS1.2, TLS1.3, HTTPS-GET result).</summary>
    public event Action<string, DiagStatus, DiagStatus, DiagStatus>? HostProbed;

    private Process? _proc;
    // Signalled when the running candidate's winws2 reports WinDivert is attached ("…capture is
    // started"), so probing starts the moment the engine is actually ready instead of after a fixed
    // delay. Recreated per StartEngine; each process's handlers capture their own instance.
    private TaskCompletionSource? _ready;

    public void Dispose() => StopEngine();

    public async Task<(ComboStrategy strategy, AutoScore score)?> RunAsync(
        IReadOnlyList<string> goalHosts, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.WinwsExe))
            throw new FileNotFoundException("Движок не установлен.");

        var candidates = ComboStrategyCatalog.All;
        ComboStrategy? best = null;
        AutoScore? bestScore = null;

        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cand = candidates[i];
            CandidateStarted?.Invoke(cand.Name);
            Status?.Invoke($"[{i + 1}/{candidates.Count}] Пробую: {cand.Name}…");

            AutoScore score;
            try
            {
                score = await EvaluateAsync(cand, goalHosts, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                score = new AutoScore(cand.Name + " — ошибка: " + ex.Message,
                    0, goalHosts.Count * 3, goalHosts.Count * 3, cand);
            }
            ScoreReady?.Invoke(score);

            // Rank by a weighted success count (a completed HTTPS GET counts far more than a bare
            // handshake), then break ties by fewest raw failures — so a config that actually loads
            // pages beats one that only handshakes and resets.
            if (bestScore is null
                || WeightedOk(score) > WeightedOk(bestScore)
                || (WeightedOk(score) == WeightedOk(bestScore) && score.Fail < bestScore.Fail))
            {
                best = cand;
                bestScore = score;
            }

            // A perfect candidate is good enough — stop early.
            if (score.Fail == 0) break;
        }

        return best is not null && bestScore is not null ? (best, bestScore) : null;
    }

    // Weighted success used only for ranking (the AutoScore shown to the user keeps raw check counts):
    // TLS 1.2 / 1.3 handshakes = 1 each, a full HTTPS GET = 3, so "the page loads" dominates the pick.
    private static int WeightedOk(AutoScore s)
        => s.HostList.Sum(r => (r.Tls12 == DiagStatus.Ok ? 1 : 0)
                             + (r.Tls13 == DiagStatus.Ok ? 1 : 0)
                             + (r.Https == DiagStatus.Ok ? 3 : 0));

    private async Task<AutoScore> EvaluateAsync(ComboStrategy cand, IReadOnlyList<string> hosts, CancellationToken ct)
    {
        StartEngine(cand);
        try
        {
            await WaitEngineReadyAsync(ct); // wait until WinDivert is actually attached (capped at the old 1500ms)
            // 3 signals per host: TLS 1.2, TLS 1.3, and a full HTTPS GET (the request must actually
            // complete, not just the handshake — a handshake-only check ranks "TLS OK but resets" too high).
            int total = hosts.Count * 3;
            if (_proc is null || _proc.HasExited)
                return new AutoScore(cand.Name, 0, total, total, cand,
                    hosts.Select(h => new AutoHostResult(h, DiagStatus.Fail, DiagStatus.Fail, DiagStatus.Fail)).ToList());

            // Probe all hosts in parallel (all three checks concurrently) — the slow part is the
            // per-probe timeout, so this turns "sum of host times" into "the slowest host".
            using var gate = new SemaphoreSlim(8);
            var probes = hosts.Select(async host =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var r = await NetProbe.ProbeHostAsync(host, ct).ConfigureAwait(false);
                    HostProbed?.Invoke(host, r.Tls12, r.Tls13, r.Https);
                    return r;
                }
                finally { gate.Release(); }
            });
            var rows = (await Task.WhenAll(probes).ConfigureAwait(false)).ToList();
            int ok = rows.Sum(r => (r.Tls12 == DiagStatus.Ok ? 1 : 0) + (r.Tls13 == DiagStatus.Ok ? 1 : 0)
                                 + (r.Https == DiagStatus.Ok ? 1 : 0));
            return new AutoScore(cand.Name, ok, total - ok, total, cand, rows);
        }
        finally
        {
            StopEngine();
            await Task.Delay(300, CancellationToken.None);
        }
    }

    private void StartEngine(ComboStrategy cand)
    {
        StopEngine(); // defensive: never leave a previous candidate's winws2 alive (two engines
                      // would fight over WinDivert and poison every subsequent probe).
        var preset = new Preset { Name = cand.Name, Args = cand.Args };
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.WinwsExe,
            WorkingDirectory = AppPaths.EngineDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // Probe with bypassAll=true: catalog candidates ARE global catch-alls by design, so the
        // goal hosts must actually be desynced during the test. (The saved preset is re-scoped by
        // ToPreset, and the allow-list safety net only applies to the running, saved preset.)
        foreach (var a in EngineService.BuildArguments(preset, null, gameFilter: false, bypassAll: true, forLaunch: true))
            psi.ArgumentList.Add(a);

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ready = ready;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Signal readiness on the WinDivert "capture is started" line; also unblock on early exit
        // (bad args) so a candidate whose engine dies instantly fails fast instead of waiting the cap.
        p.OutputDataReceived += (_, e) => { if (IsWinDivertReady(e.Data)) ready.TrySetResult(); };
        p.ErrorDataReceived += (_, e) => { if (IsWinDivertReady(e.Data)) ready.TrySetResult(); };
        p.Exited += (_, _) => ready.TrySetResult();
        _proc = p;
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch
        {
            StopEngine();
            throw;
        }
    }

    /// <summary>True for a winws2 log line that means WinDivert is attached and capturing.</summary>
    private static bool IsWinDivertReady(string? line) =>
        line is not null &&
        (line.Contains("capture is started", StringComparison.OrdinalIgnoreCase) ||
         line.Contains("windivert initialized", StringComparison.OrdinalIgnoreCase));

    /// <summary>Wait until the engine reports WinDivert is attached, capped at 1500 ms (the old fixed
    /// delay) so an absent/changed log line falls back to today's behaviour. A short settle follows so
    /// the filter is live before probing.</summary>
    private async Task WaitEngineReadyAsync(CancellationToken ct)
    {
        var ready = _ready;
        if (ready is null)
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(1500); // cap = the old fixed delay → exact fallback when no ready line appears
        try
        {
            await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            // Ready line seen: let WinDivert go live before probing (skipped on timeout — already waited the cap).
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* no ready line within cap → behave like the old fixed 1500ms delay */ }
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

    /// <summary>Build a saveable preset from a chosen combo strategy.</summary>
    public static Preset ToPreset(ComboStrategy s, AutoScope scope)
    {
        // The catalog candidate is a GLOBAL catch-all: its TLS/QUIC profiles carry no --hostlist, so
        // they desync EVERY site. Saved verbatim the running preset mangles non-listed sites (Steam,
        // dota2, …) — and since it has no --hostlist-exclude, allow-list mode ("область обхода ВЫКЛ")
        // can't scope it either. Re-route just its TLS desync bundle through the SNI-scoped combo
        // (discord/youtube hostlists + an exclude'd catch-all), exactly like the built-in/generated
        // presets, so the scope toggle governs it the same way. Generator strategies are already
        // scoped → fall back to their args verbatim if no TLS bundle is found.
        var tls = ExtractTlsBundle(s.Args);
        return new Preset
        {
            Name = $"Автоподбор: {scope.Title()} [{s.Name}]",
            Description = $"Стратегия «{s.Name}», подобранная авто-тестером как лучшая для «{scope.Title()}».",
            Args = tls.Count > 0
                ? PresetService.BuildComboArgs(tls.ToArray(), tls.ToArray(), tls.ToArray())
                : new List<string>(s.Args),
            IsBuiltIn = false,
        };
    }

    /// <summary>Pull the TLS-profile desyncs out of a catalog strategy — the --lua-desync lines inside
    /// its --filter-l7=tls profile (from that marker to the next --new). Used to re-scope a global
    /// candidate into the per-SNI combo when saving it as a preset.</summary>
    private static List<string> ExtractTlsBundle(IReadOnlyList<string> args)
    {
        var tls = new List<string>();
        bool inTls = false;
        foreach (var a in args)
        {
            if (a == "--new") { if (inTls) break; continue; }
            if (a == "--filter-l7=tls") { inTls = true; continue; }
            if (inTls && a.StartsWith("--lua-desync=", StringComparison.Ordinal)) tls.Add(a);
        }
        return tls;
    }
}
