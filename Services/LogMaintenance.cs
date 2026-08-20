using System.IO;

namespace Zapret2UI.Services;

/// <summary>
/// Housekeeping for the <c>logs\</c> folder. Every engine start opens its own <c>engine-*.log</c>
/// (<see cref="EngineService"/>); left alone these accumulate one-per-session forever. Trimming keeps the
/// newest few and drops the rest. The singleton <c>startup.log</c>/<c>fatal.log</c> are left untouched —
/// they don't grow in count and are worth keeping for diagnostics.
/// </summary>
public static class LogMaintenance
{
    /// <summary>Glob for the per-session engine logs — the only files that pile up.</summary>
    private const string SessionLogPattern = "engine-*.log";

    /// <summary>How many recent engine logs to keep when trimming on launch.</summary>
    public const int DefaultKeep = 20;

    /// <summary>Keep the newest <paramref name="keep"/> engine logs and delete the rest. Returns how many
    /// were deleted. Best-effort — a file that can't be deleted (e.g. the live session's own log) is
    /// skipped rather than throwing.</summary>
    public static int TrimOldLogs(int keep = DefaultKeep) => TrimDirectory(AppPaths.LogsDir, keep);

    /// <summary>Delete every engine log. Returns how many were deleted; the running session's log is
    /// locked and simply skipped.</summary>
    public static int ClearSessionLogs() => TrimDirectory(AppPaths.LogsDir, 0);

    // Parameterised over the directory so it can be unit-tested against a temp folder.
    internal static int TrimDirectory(string dir, int keep)
    {
        if (keep < 0) keep = 0;
        int deleted = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            var stale = new DirectoryInfo(dir).GetFiles(SessionLogPattern)
                .OrderByDescending(f => f.LastWriteTimeUtc) // newest first
                .Skip(keep);                                // everything past the keep-window is stale
            foreach (var f in stale)
            {
                try { f.Delete(); deleted++; } catch { /* locked / live log — leave it */ }
            }
        }
        catch { /* logs dir unreadable — nothing to trim */ }
        return deleted;
    }
}
