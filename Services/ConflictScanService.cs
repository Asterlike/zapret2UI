using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Zapret2UI.Services;

/// <summary>
/// One-shot startup check for software that commonly fights the DPI-bypass engine:
///   • another running DPI-bypass tool (zapret/GoodbyeDPI/ByeDPI…) — they all grab the WinDivert
///     driver, and two filters on the same packets conflict;
///   • an active VPN client or tunnel adapter — it re-routes traffic, so the desync never reaches the
///     provider and the bypass looks "broken".
/// Process-name + network-adapter based; needs no admin. Advisory only — it never blocks anything.
/// </summary>
public static class ConflictScanService
{
    // Other DPI-bypass tools — the hard conflict (shared WinDivert driver). Process names, lowercase.
    private static readonly (string proc, string label)[] DpiTools =
    {
        ("winws",        "zapret (winws) — другой обход блокировок"),
        ("winws2",       "zapret2 (winws2) — другой обход блокировок"),
        ("goodbyedpi",   "GoodbyeDPI — другой обход блокировок"),
        ("ciadpi",       "ByeDPI (ciadpi) — другой обход блокировок"),
        ("byedpi",       "ByeDPI — другой обход блокировок"),
        ("spoofdpi",     "SpoofDPI — другой обход блокировок"),
        ("dpitunnel",    "DPITunnel — другой обход блокировок"),
        ("green_tunnel", "GreenTunnel — другой обход блокировок"),
    };

    // Common VPN / proxy-tunnel clients — re-route traffic, so the engine's desync may never apply.
    private static readonly (string proc, string label)[] VpnApps =
    {
        ("openvpn",       "OpenVPN"),
        ("openvpn-gui",   "OpenVPN"),
        ("wireguard",     "WireGuard"),
        ("nordvpn",       "NordVPN"),
        ("expressvpn",    "ExpressVPN"),
        ("protonvpn",     "Proton VPN"),
        ("surfshark",     "Surfshark"),
        ("cyberghost",    "CyberGhost"),
        ("windscribe",    "Windscribe"),
        ("mullvad",       "Mullvad"),
        ("tunnelbear",    "TunnelBear"),
        ("hotspotshield", "Hotspot Shield"),
        ("hamachi",       "LogMeIn Hamachi"),
        ("radminvpn",     "Radmin VPN"),
        ("warp-svc",      "Cloudflare WARP"),
        ("outline",       "Outline"),
        ("hiddify",       "Hiddify"),
        ("nekoray",       "NekoRay"),
        ("nekobox",       "NekoBox"),
        ("v2rayn",        "v2rayN"),
        ("xray",          "Xray"),
        ("sing-box",      "sing-box"),
        ("mihomo",        "Mihomo / Clash"),
        ("clash-verge",   "Clash Verge"),
        ("clash",         "Clash"),
        ("amneziavpn",    "AmneziaVPN"),
        ("amnezia",       "Amnezia"),
        ("psiphon",       "Psiphon"),
        ("browsec",       "Browsec"),
    };

    /// <summary>Returns human-readable descriptions of every detected conflict (empty = all clear).</summary>
    public static List<string> Scan()
    {
        var hits = new List<string>();

        try
        {
            var running = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return ""; } finally { p.Dispose(); } })
                .Where(n => n.Length > 0)
                .Select(n => n.ToLowerInvariant())
                .ToHashSet();

            foreach (var (proc, label) in DpiTools)
                if (running.Contains(proc)) hits.Add(label);

            var seenVpn = new HashSet<string>();
            foreach (var (proc, label) in VpnApps)
                if (running.Contains(proc) && seenVpn.Add(label)) hits.Add("VPN: " + label);
        }
        catch { /* best-effort — a failed scan just warns about nothing */ }

        try { hits.AddRange(VpnAdapters()); } catch { /* ignore adapter enumeration failures */ }

        return hits;
    }

    // Active tunnel/VPN network adapters (WireGuard/OpenVPN-TAP/WARP…), excluding Windows' own IPv6
    // transition pseudo-tunnels so they don't raise a false alarm.
    private static IEnumerable<string> VpnAdapters()
    {
        string[] markers = { "tap-windows", "tap-nord", "wireguard", "wintun", "openvpn", "vpn",
                             "warp", "proton", "mullvad", "amnezia", "radmin vpn" };
        string[] ignore = { "isatap", "teredo", "6to4", "loopback", "kernel debug" };

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (ignore.Any(m => desc.Contains(m))) continue;

            bool isVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                         || markers.Any(m => desc.Contains(m));
            if (isVpn) yield return "Активен VPN-адаптер: " + ni.Name;
        }
    }
}
