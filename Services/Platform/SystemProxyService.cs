using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zapret2UI.Services.Platform;

/// <summary>
/// Points Windows' own proxy setting at the local WARP proxy for as long as it is running, and puts the
/// user's previous setting back afterwards.
///
/// <para><b>Why this exists.</b> The WARP proxy is a listening socket, not a tunnel: only applications
/// told about it use it. Windows' per-user proxy setting is the one place that tells nearly all of them
/// at once — Chromium browsers and anything speaking WinINET read it — without an adapter, a route or
/// administrator rights. What it cannot reach is Firefox, which keeps its own proxy setting.</para>
///
/// <para><b>Everything here is reversible and nothing is guessed.</b> The previous values are captured
/// before the write and persisted, so a crash with the setting applied is undone on the next launch
/// rather than leaving the machine pointed at a proxy that is no longer listening.</para>
/// </summary>
public static class SystemProxyService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>The value Windows has to be given for a SOCKS5 proxy on loopback.
    ///
    /// <para>The <c>socks5://</c> scheme is not decoration. WinINET's own syntax is
    /// <c>socks=host:port</c>, and Chromium reads that bare form as SOCKS <b>4</b> — measured here:
    /// with <c>socks=127.0.0.1:10800</c> the page did not load at all, while
    /// <c>socks=socks5://127.0.0.1:10800</c> came back through WARP. usque speaks SOCKS5 only, so the
    /// scheme has to be spelled out or the switch silently kills browsing instead of routing it.</para></summary>
    internal static string ProxyValue(int port) =>
        "socks=socks5://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Keep loopback out of the proxy, so the machine's own local services — the Telegram
    /// bridge, the WARP proxy itself — are still reached directly.</summary>
    private const string LocalBypass = "<local>";

    /// <summary>Point Windows at the local proxy, returning the previous state encoded for
    /// <c>settings.json</c> (see <see cref="Restore"/>). Returns null when nothing was changed, either
    /// because the write failed or because our value was already in place.</summary>
    public static string? Apply(int port)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                                    ?? throw new InvalidOperationException("Internet Settings unavailable");

            string wanted = ProxyValue(port);
            string current = key.GetValue("ProxyServer") as string ?? "";
            bool enabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0, CultureInfo.InvariantCulture) != 0;
            if (enabled && string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return null;

            string backup = Encode(enabled, current, key.GetValue("ProxyOverride") as string ?? "");

            key.SetValue("ProxyServer", wanted, RegistryValueKind.String);
            key.SetValue("ProxyOverride", LocalBypass, RegistryValueKind.String);
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            Notify();
            return backup;
        }
        catch { return null; }
    }

    /// <summary>Put back what <see cref="Apply"/> captured. Refuses to touch anything if the setting is
    /// no longer ours: the user (or another program) changed it while we were running, and their answer
    /// is the newer one.</summary>
    public static void Restore(string backup, int port)
    {
        if (backup.Length == 0) return;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                                    ?? throw new InvalidOperationException("Internet Settings unavailable");
            string current = key.GetValue("ProxyServer") as string ?? "";
            if (current.Length > 0 && !string.Equals(current, ProxyValue(port), StringComparison.OrdinalIgnoreCase))
                return;

            var (enabled, server, over) = Decode(backup);
            if (server.Length > 0) key.SetValue("ProxyServer", server, RegistryValueKind.String);
            else key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            if (over.Length > 0) key.SetValue("ProxyOverride", over, RegistryValueKind.String);
            else key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            Notify();
        }
        catch { /* best-effort: a setting we cannot write back is not worth failing the shutdown over */ }
    }

    // ---- the persisted backup -------------------------------------------------------------------

    /// <summary>"1|http=host:port|&lt;local&gt;" — one readable line in settings.json rather than three
    /// fields, because it is recovery data and not a preference anyone edits.</summary>
    internal static string Encode(bool enabled, string server, string over) =>
        (enabled ? "1|" : "0|") + server.Replace('|', ' ') + "|" + over.Replace('|', ' ');

    internal static (bool Enabled, string Server, string Override) Decode(string backup)
    {
        string[] parts = backup.Split('|');
        return (parts.Length > 0 && parts[0] == "1",
                parts.Length > 1 ? parts[1] : "",
                parts.Length > 2 ? parts[2] : "");
    }

    // ---- telling Windows the setting moved ------------------------------------------------------

    /// <summary>WinINET caches the proxy configuration per process, so a registry write alone leaves
    /// already-running applications on the old setting. Chromium watches the key itself and does not
    /// need this; everything else does.</summary>
    private static void Notify()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { /* the setting is written either way */ }
    }

    private const int INTERNET_OPTION_REFRESH = 37;
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
