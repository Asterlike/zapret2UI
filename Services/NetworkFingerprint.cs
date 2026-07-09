using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Zapret2UI.Services;

/// <summary>
/// A stable, LOCAL identifier for the network the machine is currently on — computed with NO external
/// calls (fits a privacy tool): the active interface's IPv4 gateway + that gateway's MAC (via ARP) +
/// the local /24. Lets the app remember which bypass strategy worked on a given network (home Wi-Fi vs
/// mobile vs work) and re-suggest it when the user returns. Returns null when offline / indeterminate.
/// The gateway MAC keeps two different ISPs that both use 192.168.1.0/24 from colliding; a rare
/// collision only costs a wrong preselection, which the user fixes by re-running the picker.
/// </summary>
public static class NetworkFingerprint
{
    public static string? Current()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var props = nic.GetIPProperties();
                var gw = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                if (gw is null) continue;

                var local = props.UnicastAddresses
                    .Select(u => u.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

                string raw = $"{gw}|{GatewayMac(gw)}|{Subnet24(local)}";
                return Hash(raw);
            }
        }
        catch { /* offline / no permission — treat as no fingerprint */ }
        return null;
    }

    private static string Subnet24(IPAddress? ip)
    {
        if (ip is null) return "";
        var b = ip.GetAddressBytes();
        return b.Length == 4 ? $"{b[0]}.{b[1]}.{b[2]}.0" : "";
    }

    private static string GatewayMac(IPAddress gateway)
    {
        try
        {
            byte[] mac = new byte[6];
            int len = mac.Length;
            int dest = BitConverter.ToInt32(gateway.GetAddressBytes(), 0);
            if (SendARP(dest, 0, mac, ref len) == 0 && len >= 6)
                return Convert.ToHexString(mac, 0, 6);
        }
        catch { /* ARP unavailable — gateway IP + subnet still identify most networks */ }
        return "";
    }

    private static string Hash(string s)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 6); // 12 hex chars — plenty to key distinct networks
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int macAddrLen);
}
