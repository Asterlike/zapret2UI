using System.IO;
using System.Text;
using System.Windows;
using Zapret2UI.Services.Telegram;

namespace Zapret2UI.Harness;

/// <summary>
/// Headless checks for the Telegram proxy. Both need no administrator — the proxy is loopback plus
/// outbound TLS — so a user whose proxy "pings but won't connect" can be diagnosed without elevating
/// anything, and neither check can disturb a copy of the app already running.
/// </summary>
internal static class TelegramSelfTest
{
    /// <summary><c>--tgproxytest &lt;outFile&gt;</c>: probe the upstream paths to Telegram (DoH / DNS /
    /// direct IP / Cloudflare fronts) and write the report. Mirrors what the in-app «Проверить
    /// соединение» button does, but headless.</summary>
    internal static async Task RunUpstreamAsync(string outFile)
    {
        var sb = new StringBuilder();
        try
        {
            using var svc = new TelegramProxyService();
            svc.LogLine += line => sb.AppendLine(line);
            await svc.SelfTestAsync();
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally { Finish(outFile, sb); }
    }

    /// <summary><c>--tgbridgetest &lt;outFile&gt;</c>: drive the REAL bridge from a loopback client and
    /// check that Telegram's resPQ survives the round-trip decodable — which separates a bridge bug
    /// (re-encryption, packet splitting) from a censored network dropping the connection.</summary>
    internal static async Task RunBridgeAsync(string outFile)
    {
        var sb = new StringBuilder();
        TelegramProxyService? svc = null;
        try
        {
            svc = new TelegramProxyService();
            svc.LogLine += line => sb.AppendLine(line);
            svc.Verbose = true; // per-connection lines: which upstream each lane took, volume and rate
            svc.Start();
            await Task.Delay(400);
            // Both lanes: chat (positive DC) and media (negative). They take different upstreams by
            // design — chat the direct Telegram edge, media the Cloudflare fronts — so testing only
            // the chat one would leave the path file transfers use unverified.
            await svc.BridgeSelfTestAsync(2);
            await svc.BridgeSelfTestAsync(-2);
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally
        {
            try { svc?.Stop(); } catch { /* ignore */ }
            Finish(outFile, sb);
        }
    }

    private static void Finish(string outFile, StringBuilder sb)
    {
        try { File.WriteAllText(outFile, sb.ToString()); } catch { /* best effort */ }
        Application.Current.Shutdown(0);
    }
}
