using System.Diagnostics;
using System.IO;
using System.Security.Authentication;
using System.Text;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>Per-endpoint outcome of a candidate (TLS 1.2 / 1.3).</summary>
public sealed record AutoHostResult(string Host, DiagStatus Tls12, DiagStatus Tls13);

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
    /// <summary>Fired after each goal host is probed (host, TLS1.2 result, TLS1.3 result).</summary>
    public event Action<string, DiagStatus, DiagStatus>? HostProbed;

    private Process? _proc;

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
                    0, goalHosts.Count * 2, goalHosts.Count * 2, cand);
            }
            ScoreReady?.Invoke(score);

            if (bestScore is null || score.Fail < bestScore.Fail ||
                (score.Fail == bestScore.Fail && score.Ok > bestScore.Ok))
            {
                best = cand;
                bestScore = score;
            }

            // A perfect candidate is good enough — stop early.
            if (score.Fail == 0) break;
        }

        return best is not null && bestScore is not null ? (best, bestScore) : null;
    }

    private async Task<AutoScore> EvaluateAsync(ComboStrategy cand, IReadOnlyList<string> hosts, CancellationToken ct)
    {
        StartEngine(cand);
        try
        {
            await Task.Delay(1500, ct); // let WinDivert attach
            int total = hosts.Count * 2;
            if (_proc is null || _proc.HasExited)
                return new AutoScore(cand.Name, 0, total, total, cand,
                    hosts.Select(h => new AutoHostResult(h, DiagStatus.Fail, DiagStatus.Fail)).ToList());

            // Probe all hosts in parallel (both TLS versions concurrently) — the slow part is the
            // per-probe timeout, so this turns "sum of host times" into "the slowest host".
            using var gate = new SemaphoreSlim(8);
            var probes = hosts.Select(async host =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var p12 = NetProbe.TlsAsync(host, SslProtocols.Tls12, ct);
                    var p13 = NetProbe.TlsAsync(host, SslProtocols.Tls13, ct);
                    await Task.WhenAll(p12, p13).ConfigureAwait(false);
                    HostProbed?.Invoke(host, p12.Result, p13.Result);
                    return new AutoHostResult(host, p12.Result, p13.Result);
                }
                finally { gate.Release(); }
            });
            var rows = (await Task.WhenAll(probes).ConfigureAwait(false)).ToList();
            int ok = rows.Sum(r => (r.Tls12 == DiagStatus.Ok ? 1 : 0) + (r.Tls13 == DiagStatus.Ok ? 1 : 0));
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
        foreach (var a in EngineService.BuildArguments(preset, null)) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
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
    public static Preset ToPreset(ComboStrategy s, AutoScope scope) => new()
    {
        Name = $"Автоподбор: {scope.Title()} [{s.Name}]",
        Description = $"Стратегия «{s.Name}», подобранная авто-тестером как лучшая для «{scope.Title()}».",
        Args = new List<string>(s.Args),
        IsBuiltIn = false,
    };
}
