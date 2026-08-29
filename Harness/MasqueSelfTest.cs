using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using Zapret2UI.Services.Warp;

namespace Zapret2UI.Harness;

/// <summary>
/// Headless checks for the MASQUE proxy. Proxy mode needs no adapter, no routes and no elevation, so
/// unlike everything about the old WireGuard tunnel these settle the questions BEFORE a screen is
/// built around them — and a failure here cannot take the machine off the network.
/// </summary>
internal static class MasqueSelfTest
{
    /// <summary><c>--masquetest [exitPort]</c>: register a device if needed, raise the local proxy and
    /// ask Cloudflare — THROUGH that proxy — where it thinks we are.
    ///
    /// <para>Walks the rest of the MASQUE ports on failure, because which port survives is exactly what
    /// differs between networks, and a single "no" would say nothing.</para></summary>
    internal static async Task RunAsync(int exitPort)
    {
        var sb = new StringBuilder();
        var masque = new MasqueService();
        masque.LogLine += line => sb.AppendLine("  " + line);
        try
        {
            if (MasqueService.IsRegistered)
            {
                sb.AppendLine("register: a device is already enrolled, reusing it");
            }
            else
            {
                var reg = await masque.RegisterAsync();
                sb.AppendLine("register: " + (reg.Ok ? "OK" : "FAILED — " + reg.Message));
                if (!reg.Ok) return;
            }
            sb.AppendLine();

            // Drive the SAME sweep the button will, so this harness keeps testing the shipped path rather
            // than a parallel one that could quietly drift away from it.
            var (result, winner) = await masque.ConnectAsync(FreeLoopbackPort(), preferHttp2: true, exitPort);
            masque.Stop();

            sb.AppendLine();
            sb.AppendLine(result.Ok
                ? $"WORKS via {winner?.Describe()} — {result.Message}"
                : $"FAILED — {result.Message}");
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally { Finish(masque, "masquetest.txt", "MASQUE test", sb); }
    }

    /// <summary><c>--masqueregion</c>: try every entry point MASQUE offers and report which country each
    /// one comes out in.
    ///
    /// <para>The pool is small and fixed — two IPv4 addresses and two IPv6 blocks — so this is a complete
    /// sweep rather than a sample. The node and the country are different things and both are printed: a
    /// tunnel can run through Frankfurt and still be seen as Russia, and only the country matters to a
    /// geo block.</para></summary>
    internal static async Task RunRegionScanAsync()
    {
        var sb = new StringBuilder();
        var masque = new MasqueService();
        try
        {
            if (!MasqueService.IsRegistered)
            {
                var reg = await masque.RegisterAsync();
                if (!reg.Ok) { sb.AppendLine("register FAILED — " + reg.Message); return; }
            }

            // HTTP/2 on 443 throughout: measured as the one that reliably connects here, so the only
            // thing varying between rows is the entry point itself.
            MasqueTransport[] pool =
            [
                new(Http2: true, ConnectPort: 443, Endpoint: "162.159.198.2"),
                new(Http2: true, ConnectPort: 443, Endpoint: "162.159.198.1"),
                new(Http2: true, ConnectPort: 443, Endpoint: "2606:4700:103::2", Ipv6: true),
                new(Http2: true, ConnectPort: 443, Endpoint: "2606:4700:103::1", Ipv6: true),
                new(Http2: true, ConnectPort: 443, Endpoint: "2606:4700:104::2", Ipv6: true),
                new(Http2: true, ConnectPort: 443, Endpoint: "2606:4700:104::1", Ipv6: true),
            ];

            sb.AppendLine($"{"entry point",-22} {"country",-9} {"node",-6} exit address");
            sb.AppendLine(new string('-', 62));

            foreach (var t in pool)
            {
                var r = await masque.StartAsync(FreeLoopbackPort(), t);
                var x = masque.LastExit;
                sb.AppendLine(r.Ok && x is { } e
                    ? $"{t.Endpoint,-22} {e.Location,-9} {e.Colo,-6} {e.Ip}"
                    : $"{t.Endpoint,-22} —         —      не подключилось");
                masque.Stop();
            }
        }
        catch (Exception ex) { sb.AppendLine("EXC: " + ex); }
        finally { Finish(masque, "masqueregion.txt", "MASQUE region scan", sb); }
    }

    /// <summary>A loopback port nothing is listening on. Asked of the OS rather than guessed: 1080 is the
    /// conventional SOCKS port and is often already taken by whatever else the user runs.</summary>
    private static int FreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void Finish(MasqueService masque, string outFile, string caption, StringBuilder sb)
    {
        masque.Dispose();
        string text = sb.ToString();
        try { File.WriteAllText(outFile, text); } catch { /* best effort */ }
        MessageBox.Show(text, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        Application.Current.Shutdown(0);
    }
}
