using System.IO;
using System.Text;
using System.Windows;
using Zapret2UI.Services.Engine;
using Zapret2UI.Services.Infrastructure;
using Zapret2UI.Services.Strategies;

namespace Zapret2UI.Harness;

/// <summary>
/// <c>Zapret2UI.exe --enginedump &lt;outFile&gt;</c> — seed the bundled lists and write out the winws2
/// command line for the recommended strategy, then exit.
///
/// <para>Building the arguments needs no administrator; only RUNNING the engine does. So this is the
/// one way to inspect what the engine would actually be told — the always-on coverage profiles, the
/// blob declarations, the seeded lists — from an ordinary shell. It verifies argument construction,
/// never desync. Mirrors the command-line preview in Настройки.</para>
/// </summary>
internal static class EngineDump
{
    internal static void Run(string outFile)
    {
        var sb = new StringBuilder();
        try
        {
            AppPaths.EnsureCreated();
            var hostlists = new HostlistService();
            hostlists.SeedDefaults(); // writes lists/tgproxy-fronts.txt from the proxy balancer
            var presets = new PresetService();
            var rec = presets.All.FirstOrDefault(p => p.IsRecommended) ?? presets.All[0];

            sb.AppendLine("# preset: " + rec.Name);
            sb.AppendLine(EngineService.PreviewCommandLine(rec, null));
            sb.AppendLine();
            // Same preset with the Журнал tab's verbose switch on — lets the --debug=1 flag be
            // verified without elevation (the engine still needs admin to actually run).
            sb.AppendLine("# with verbose log:");
            sb.AppendLine(EngineService.PreviewCommandLine(rec, null, debugLog: true));
            sb.AppendLine();
            sb.AppendLine($"# tgproxy-fronts.txt ({hostlists.ReadDomains("tgproxy-fronts").Count} domains):");
            sb.AppendLine(hostlists.Read("tgproxy-fronts"));
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally
        {
            try { File.WriteAllText(outFile, sb.ToString()); } catch { /* best effort */ }
            Application.Current.Shutdown(0);
        }
    }
}
