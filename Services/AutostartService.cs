using System.Diagnostics;

namespace ZapretUI.Services;

/// <summary>
/// Manages a Scheduled Task that launches the (elevated) UI at logon.
/// A plain Run registry key cannot start an app elevated without a UAC prompt,
/// so a task with "highest privileges" is used instead.
/// </summary>
public sealed class AutostartService
{
    private const string TaskName = "ZapretUI Autostart";

    private static string? ExePath => Environment.ProcessPath;

    public bool IsSupported =>
        ExePath is not null &&
        ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !ExePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase);

    public bool IsEnabled()
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public bool Enable()
    {
        if (!IsSupported || ExePath is null) return false;
        // /RL HIGHEST = run with highest privileges; /TR runs the UI in tray mode.
        var (code, _) = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExePath}\\\" --tray\" " +
            "/SC ONLOGON /RL HIGHEST /F");
        return code == 0;
    }

    public bool Disable()
    {
        var (code, _) = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        return code == 0;
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
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
