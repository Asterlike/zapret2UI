using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Zapret2UI.Localization;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Services.Platform;

/// <summary>
/// Manages a Scheduled Task that launches the (elevated) UI at logon.
/// A plain Run registry key cannot start an app elevated without a UAC prompt,
/// so a task with "highest privileges" is used instead.
/// </summary>
public sealed class AutostartService
{
    private const string TaskName = "Zapret2UI Autostart";

    private static string? ExePath => Environment.ProcessPath;

    public bool IsSupported =>
        ExePath is not null &&
        ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !ExePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Register the logon task, replacing the one already there. It is written out as XML because the
    /// schtasks switches leave the rest of the definition at Task Scheduler's defaults — the defaults for
    /// a background job: no start on battery, STOPPED when the charger is unplugged, killed after 72
    /// hours, and priority 7, i.e. below-normal CPU with low I/O and memory priority, which every process
    /// the app starts inherits, winws2 included. For a program meant to sit in the tray all day each of
    /// those is a way to lose the bypass without a word.
    /// </summary>
    public bool Enable()
    {
        if (!IsSupported || ExePath is null) return false;
        string xmlFile = Path.Combine(AppPaths.TempDir, "autostart-task.xml");
        try
        {
            AppPaths.EnsureCreated();
            // UTF-16 with a BOM, as the declaration inside says — the form Task Scheduler exports itself.
            File.WriteAllText(xmlFile, BuildTaskXml(ExePath, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
            var (code, _) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlFile}\" /F");
            return code == 0;
        }
        catch { return false; }
        finally { try { File.Delete(xmlFile); } catch { /* a leftover temp file is harmless */ } }
    }

    public bool Disable()
    {
        var (code, _) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0;
    }

    /// <summary>
    /// The task: at this user's logon, run the app with <c>--tray</c> (it starts in the tray, without a
    /// window), elevated without a prompt, for as long as it runs, on battery too, at the priority a
    /// program started from the Start menu gets (4). Pure, so the settings that matter are pinned by a test.
    /// </summary>
    internal static string BuildTaskXml(string exePath, string user)
    {
        string exe = SecurityElement.Escape(exePath);
        string who = SecurityElement.Escape(user);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{who}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{who}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>--tray</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            // Drain both pipes concurrently (in the background) so neither can stall the other, then
            // wait for exit — reading only one pipe sequentially is the classic two-pipe deadlock.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, Loc.T("schtasks не ответил вовремя."));
            }
            string output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
