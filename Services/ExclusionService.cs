using System.Diagnostics;

namespace Zapret2UI.Services;

/// <summary>
/// One-click "make Windows leave us alone": adds Windows Defender exclusions (for the app, the whole
/// <c>%LOCALAPPDATA%\Zapret2UI</c> data folder and the engine folder + processes) and Windows Firewall
/// allow-rules (in/out, all profiles) for the app and <c>winws2.exe</c>. A DPI-bypass engine ships an
/// unsigned kernel driver (WinDivert) that antivirus loves to quarantine, and a blocking firewall is a
/// common cause of "logs in but won't connect"; this fixes both in one go.
///
/// Every step shells out to <c>powershell Add-MpPreference</c> / <c>netsh advfirewall</c>, which need
/// admin — the app already runs elevated (requireAdministrator manifest). All steps are best-effort and
/// idempotent (firewall rules are deleted-then-readded; Defender exclusions are no-ops if present).
/// </summary>
public sealed class ExclusionService
{
    /// <summary>Outcome of an apply run: whether every step succeeded + a human, per-step summary.</summary>
    public sealed record Result(bool AllOk, string Summary);

    /// <summary>Apply all Defender exclusions and firewall rules. Safe to call repeatedly.</summary>
    public async Task<Result> ApplyAsync()
    {
        var log = new List<string>();
        bool ok = true;

        string? appExe = AppExePath();
        string root = AppPaths.Root;
        string engineDir = AppPaths.EngineDir;
        string winws = AppPaths.WinwsExe;

        // 1) Windows Defender — exclude the data + engine folders and the two executables.
        ok &= await DefenderExcludePathAsync(root, log);
        ok &= await DefenderExcludePathAsync(engineDir, log);
        ok &= await DefenderExcludeProcessAsync(winws, log);
        if (appExe is not null) ok &= await DefenderExcludeProcessAsync(appExe, log);

        // 2) Windows Firewall — allow the app and the engine through (inbound + outbound, all profiles).
        ok &= await FirewallAllowAsync("Zapret2UI (движок)", winws, log);
        if (appExe is not null) ok &= await FirewallAllowAsync("Zapret2UI", appExe, log);

        return new Result(ok, string.Join('\n', log));
    }

    /// <summary>The app's own .exe, or null when running as `dotnet …` in development (excluding
    /// dotnet.exe would be meaningless / wrong, so we skip the app-specific steps there).</summary>
    private static string? AppExePath()
    {
        string? p = Environment.ProcessPath;
        if (p is null) return null;
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (p.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;
        return p;
    }

    private static async Task<bool> DefenderExcludePathAsync(string path, List<string> log)
    {
        var (code, output) = await RunAsync("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            // -ErrorAction Stop makes a failure (Defender off / third-party AV) terminating, so
            // powershell.exe exits non-zero and we report it honestly instead of a false success.
            $"Add-MpPreference -ExclusionPath '{PsQuote(path)}' -ErrorAction Stop",
        });
        return Note(log, code == 0, $"Defender · папка «{path}»", output);
    }

    private static async Task<bool> DefenderExcludeProcessAsync(string exe, List<string> log)
    {
        var (code, output) = await RunAsync("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            $"Add-MpPreference -ExclusionProcess '{PsQuote(exe)}' -ErrorAction Stop",
        });
        return Note(log, code == 0, $"Defender · процесс «{System.IO.Path.GetFileName(exe)}»", output);
    }

    private static async Task<bool> FirewallAllowAsync(string name, string exe, List<string> log)
    {
        // Drop any prior rules with this name first, so clicking again doesn't pile up duplicates.
        await RunAsync("netsh.exe", new[] { "advfirewall", "firewall", "delete", "rule", $"name={name}" });
        var (cIn, oIn) = await RunAsync("netsh.exe", new[]
        {
            "advfirewall", "firewall", "add", "rule",
            $"name={name}", "dir=in", "action=allow", $"program={exe}", "enable=yes", "profile=any",
        });
        var (cOut, oOut) = await RunAsync("netsh.exe", new[]
        {
            "advfirewall", "firewall", "add", "rule",
            $"name={name}", "dir=out", "action=allow", $"program={exe}", "enable=yes", "profile=any",
        });
        return Note(log, cIn == 0 && cOut == 0, $"Брандмауэр · «{name}»", oIn + " " + oOut);
    }

    /// <summary>Escape a single-quoted PowerShell string (double any embedded single quotes).</summary>
    private static string PsQuote(string s) => s.Replace("'", "''");

    private static bool Note(List<string> log, bool ok, string what, string output)
    {
        log.Add(ok ? $"✓ {what}" : $"✗ {what} — {Short(output)}");
        return ok;
    }

    private static string Short(string s)
    {
        string t = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (t.Length == 0) return "нет вывода";
        return t.Length > 140 ? t[..140] + "…" : t;
    }

    private static async Task<(int code, string output)> RunAsync(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            // Drain both pipes concurrently (background) so neither can stall the other, then wait.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "истекло время ожидания");
            }
            string output = (await outTask) + (await errTask);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
