using System.Diagnostics;

namespace ZapretUI.Services;

/// <summary>
/// Ensures Windows TCP timestamps (RFC 1323) are ENABLED while the engine runs, then restores the
/// previous setting on stop.
///
/// Why this matters: the <c>tcp_ts</c> / <c>tcp_ts_up</c> fooling used by most presets (ALT10/ALT11
/// rely on <c>tcp_ts</c> as their ONLY fooling) can only work if the outgoing TCP packet already
/// carries a timestamp option — the engine's <c>apply_fooling</c> silently does NOTHING otherwise
/// ("timestamp tcp option not present or invalid"). Windows leaves timestamps at "allowed" by
/// default, which does NOT guarantee them on client-initiated connections, so on a stock box the
/// fake never dies and corrupts the real connection. The upstream tooling (blockcheck2 / Flowseal)
/// flips this to "enabled" via netsh; we do the same, scoped to the engine session.
///
/// Best-effort: netsh needs admin (the app already runs elevated). A failure just leaves ts-fooling
/// ineffective — same as before — so we never block the engine on it.
/// </summary>
public sealed class TcpTimestampsService
{
    // The English netsh keyword to restore on Stop, or null when we didn't change anything
    // (already "enabled", or the enable failed). netsh SET keywords are never localised.
    private string? _restoreTo;

    /// <summary>Enable TCP timestamps for the session unless already enabled, remembering the prior value.</summary>
    public void EnableForSession(Action<string>? log = null)
    {
        try
        {
            string? current = ReadState();                 // "enabled" | "allowed" | "disabled" | null
            if (string.Equals(current, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                _restoreTo = null;                          // already on — leave it, nothing to undo
                return;
            }
            // Restore to the detected prior value, or Windows' default "allowed" if we couldn't parse it.
            _restoreTo = current ?? "allowed";
            if (SetState("enabled"))
                log?.Invoke("TCP timestamps: включены на время сеанса (нужны, чтобы ts-fooling работал).");
            else
                _restoreTo = null;                          // set failed — don't try to "restore" later
        }
        catch { _restoreTo = null; }
    }

    /// <summary>Restore the timestamps setting captured by <see cref="EnableForSession"/> (no-op if unchanged).</summary>
    public void RestoreAfterSession(Action<string>? log = null)
    {
        string? target = _restoreTo;
        _restoreTo = null;
        if (target is null) return;
        try
        {
            if (SetState(target))
                log?.Invoke($"TCP timestamps: возвращены в исходное состояние ({target}).");
        }
        catch { /* best-effort */ }
    }

    /// <summary>Read the global RFC 1323 timestamps state, normalised to the English netsh keyword.</summary>
    private static string? ReadState()
    {
        string outp = RunNetsh("interface", "tcp", "show", "global");
        // "1323" is locale-independent; the value word may be localised, so map known tokens (en/ru)
        // to the English keyword. Unknown → null (caller falls back to the "allowed" default).
        foreach (var raw in outp.Replace("\r\n", "\n").Split('\n'))
        {
            if (!raw.Contains("1323", StringComparison.Ordinal)) continue;
            int colon = raw.IndexOf(':');
            string val = (colon >= 0 ? raw[(colon + 1)..] : raw).Trim().ToLowerInvariant();
            if (val.Contains("disab") || val.Contains("откл") || val.Contains("выкл")) return "disabled";
            if (val.Contains("enab")  || val.Contains("включ"))                         return "enabled";
            if (val.Contains("allow") || val.Contains("разреш"))                        return "allowed";
            return null;
        }
        return null;
    }

    /// <summary><paramref name="keyword"/> is an English netsh literal (enabled/allowed/disabled) — safe across locales.</summary>
    private static bool SetState(string keyword)
    {
        try
        {
            using var p = Start("interface", "tcp", "set", "global", $"timestamps={keyword}");
            if (p is null) return false;
            if (!p.WaitForExit(5000)) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string RunNetsh(params string[] args)
    {
        try
        {
            using var p = Start(args);
            if (p is null) return "";
            string outp = p.StandardOutput.ReadToEnd();     // small (~15 lines), no deadlock risk
            if (!p.WaitForExit(5000)) { try { p.Kill(entireProcessTree: true); } catch { } }
            return outp;
        }
        catch { return ""; }
    }

    private static Process? Start(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
