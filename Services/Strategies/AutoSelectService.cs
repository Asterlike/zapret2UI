using System.IO;
using Zapret2UI.Localization;
using Zapret2UI.Models;
using Zapret2UI.Services.Engine;
using Zapret2UI.Services.Infrastructure;
using Zapret2UI.Services.Network;

namespace Zapret2UI.Services.Strategies;

/// <summary>Per-endpoint outcome of a candidate (TLS 1.2 / 1.3 + full HTTPS GET).</summary>
public sealed record AutoHostResult(string Host, DiagStatus Tls12, DiagStatus Tls13, DiagStatus Https);

/// <summary>Score of one candidate against the goal endpoints (lower Fail = better).</summary>
public sealed record AutoScore(
    string Name, int Ok, int Fail, int Total,
    ComboStrategy? Strategy = null, IReadOnlyList<AutoHostResult>? Hosts = null)
{
    public string Detail => Fail == 0 ? Loc.T("всё прошло ({0}/{1})", Ok, Total) : Loc.T("{0}/{1} прошло, ошибок: {2}", Ok, Total, Fail);
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

    // Throwaway winws2 per candidate — start, wait for WinDivert, kill. Shared with the generator.
    private readonly ProbeEngineRunner _runner = new();

    public void Dispose() => _runner.Dispose();

    public async Task<(ComboStrategy strategy, AutoScore score)?> RunAsync(
        IReadOnlyList<ComboStrategy> candidates, IReadOnlyList<string> goalHosts, CancellationToken ct)
    {
        if (!File.Exists(AppPaths.WinwsExe))
            throw new FileNotFoundException(Loc.T("Движок не установлен."));

        ComboStrategy? best = null;
        AutoScore? bestScore = null;

        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cand = candidates[i];
            CandidateStarted?.Invoke(cand.Name);
            Status?.Invoke(Loc.T("[{0}/{1}] Пробую: {2}…", i + 1, candidates.Count, Loc.T(cand.Name)));

            AutoScore score;
            try
            {
                score = await EvaluateAsync(cand, goalHosts, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                score = new AutoScore(Loc.T(cand.Name) + Loc.T(" — ошибка: ") + ex.Message,
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
            await _runner.WaitReadyAsync(ct); // until WinDivert is attached (capped at the old 1500ms)
            // 3 signals per host: TLS 1.2, TLS 1.3, and a full HTTPS GET (the request must actually
            // complete, not just the handshake — a handshake-only check ranks "TLS OK but resets" too high).
            int total = hosts.Count * 3;
            if (!_runner.IsAlive)
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
            _runner.Stop();
            await Task.Delay(300, CancellationToken.None);
        }
    }

    private void StartEngine(ComboStrategy cand)
    {
        var preset = new Preset { Name = cand.Name, Args = cand.Args };
        // Global catalog candidates are catch-alls → probe with bypassAll=true so the goal hosts are
        // actually desynced. A scoped preset candidate (saved-for-network / built-in) carries its own
        // hostlists → probe as it really runs, bypassAll=false. cand.BypassAll encodes which.
        _runner.Start(EngineService.BuildArguments(
            preset, null, gameFilter: false, bypassAll: cand.BypassAll, forLaunch: true));
    }

    /// <summary>Build a saveable preset from a chosen combo strategy.</summary>
    public static Preset ToPreset(ComboStrategy s, AutoScope scope)
    {
        // A ready preset candidate (the network-saved one or a built-in) won as-is: return it
        // directly — nothing to re-route, and the caller just selects the existing preset.
        if (s.SourcePreset is not null) return s.SourcePreset;

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
            Name = Loc.T("Автоподбор: {0} [{1}]", scope.Title(), Loc.T(s.Name)),
            Description = Loc.T("Стратегия «{0}», подобранная авто-тестером как лучшая для «{1}».", Loc.T(s.Name), scope.Title()),
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
