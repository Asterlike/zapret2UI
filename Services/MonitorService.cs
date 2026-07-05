using System.Security.Authentication;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Lightweight background watchdog for the auto-orchestrator. While the engine
/// runs it periodically TLS-probes a couple of key endpoints; after a sustained
/// failure it raises <see cref="ConnectivityLost"/> so the app can silently
/// re-pick a strategy and self-heal. Cheap and quiet — one probe per target per tick.
/// </summary>
public sealed class MonitorService
{
    private static readonly string[] Watch = { "gateway.discord.gg", "www.youtube.com" };
    private const int TickSeconds = 45;
    private const int FailsToHeal = 2;     // ~90s of failure before acting
    private const int BackoffSeconds = 120; // pause after a heal request

    /// <summary>Raised (once per episode) when watched endpoints stay unreachable.</summary>
    public event Action? ConnectivityLost;
    /// <summary>Raised every tick with the current health (for a subtle indicator).</summary>
    public event Action<bool>? Tick;

    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts is not null;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
    }

    public void Stop()
    {
        // Cancel but don't Dispose: the running LoopAsync may still be awaiting Task.Delay on this
        // token, and disposing the CTS out from under it can surface as ObjectDisposedException
        // instead of a clean cancel. No CancelAfter/wait-handle is used, so GC reclaims the CTS.
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        int fails = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds), ct);
                bool ok = await HealthyAsync(ct);
                // Isolate subscriber callbacks: a throw from a UI handler (marshaled synchronously) must
                // not kill the watchdog loop or surface as an unobserved task exception.
                try { Tick?.Invoke(ok); } catch { /* subscriber threw — keep watching */ }
                if (ok) { fails = 0; continue; }

                if (++fails >= FailsToHeal)
                {
                    fails = 0;
                    try { ConnectivityLost?.Invoke(); } catch { /* subscriber threw — keep watching */ }
                    await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds), ct); // let the heal settle
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (ObjectDisposedException) { /* CTS torn down during stop */ }
    }

    /// <summary>Healthy when every watched endpoint completes a TLS handshake.</summary>
    private static async Task<bool> HealthyAsync(CancellationToken ct)
    {
        foreach (var host in Watch)
        {
            if (await NetProbe.TlsAsync(host, SslProtocols.Tls12, ct) != DiagStatus.Ok)
                return false;
        }
        return true;
    }
}
