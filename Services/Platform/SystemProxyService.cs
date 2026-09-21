using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>A PAC carried in the setting itself instead of fetched from a server.
    ///
    /// <para>MEASURED, and the reason this is not an HTTP listener: with the PAC served from a local
    /// port, a browser that cannot fetch it falls back to <b>DIRECT</b>. So the moment this app crashed,
    /// every routed site would quietly go out from the user's own address — the exact leak the routing
    /// exists to prevent, arriving silently at the worst possible moment. A PAC inside the URL has no
    /// server to lose. Verified through Windows' own setting in Brave and Edge.</para></summary>
    internal const string PacPrefix = "data:application/x-ns-proxy-autoconfig;base64,";

    /// <summary>How long the whole <c>data:</c> URL may get. Windows hands the setting back through a
    /// fixed URL buffer (2048 characters), and a PAC cut in half does not fail loudly — it fails to
    /// parse, and a browser with an unparseable PAC goes DIRECT. The margin is deliberate.</summary>
    internal const int PacUrlLimit = 1900;

    /// <summary>The routing rule itself: the listed names to one proxy, everything else to another one —
    /// or straight out.
    ///
    /// <para>Two ports, because there are two proxies. The chain that changes the country is slow by
    /// nature (Tor), so it carries only the sites that need it, while the ordinary WARP proxy — when the
    /// user asked for «весь трафик системы» — carries the rest at full speed. Either port may be 0,
    /// which means «that half is not routed».</para>
    ///
    /// <para>The routed names get their proxy and <b>no <c>DIRECT</c> after it</b>. That is the whole
    /// safety property: with the proxy down the site fails to open instead of loading from the user's
    /// own address, which for a site that refuses by country is the difference between «сейчас не
    /// работает» and a session opened from the country they were hiding.</para></summary>
    /// <param name="routedPort">Proxy for the listed names; 0 leaves them unrouted.</param>
    /// <param name="domains">The names that go to <paramref name="routedPort"/>.</param>
    /// <param name="everythingPort">Proxy for every other host; 0 sends them out directly.</param>
    internal static string BuildPac(int routedPort, IReadOnlyList<string> domains, int everythingPort,
                                    IReadOnlyList<string>? bypass = null)
    {
        string Proxy(int port) => "\"SOCKS5 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "\"";
        string tail = everythingPort > 0 ? Proxy(everythingPort) : "\"DIRECT\"";

        var script = new StringBuilder();
        script.Append("function FindProxyForURL(url, host){")
              .Append("var h=(\"\"+host).toLowerCase();")
              // Same exception WinINET's own <local> makes: the machine's own services stay local.
              .Append("if(isPlainHostName(h)||h==\"localhost\"||h==\"127.0.0.1\"||h==\"::1\")return \"DIRECT\";");

        var names = new List<string>();
        if (routedPort > 0)
            foreach (string domain in domains)
            {
                string name = Sanitise(domain);
                if (name.Length > 0) names.Add(name);
            }

        if (names.Count == 0) return script.Append("return ").Append(tail).Append(";}").ToString();

        // The exceptions are tested FIRST and there is no other order that works: the routed names match
        // by suffix, so cdn.grok.com would be caught by grok.com and never reach a test that came after.
        var past = new List<string>();
        foreach (string domain in bypass ?? Array.Empty<string>())
        {
            string name = Sanitise(domain);
            if (name.Length > 0) past.Add(name);
        }

        if (past.Count > 0)
            script.Append("var b=[").Append(string.Join(",", past.Select(n => "\"" + n + "\""))).Append("];")
                  .Append("for(var i=0;i<b.length;i++){var e=b[i];")
                  .Append("if(h==e||h.slice(-e.length-1)==\".\"+e)return ").Append(tail).Append(";}");

        script.Append("var P=").Append(Proxy(routedPort)).Append(';')
              .Append("var d=[").Append(string.Join(",", names.Select(n => "\"" + n + "\""))).Append("];")
              .Append("for(var i=0;i<d.length;i++){var s=d[i];")
              .Append("if(h==s||h.slice(-s.length-1)==\".\"+s)return P;}");

        return script.Append("return ").Append(tail).Append(";}").ToString();
    }

    /// <summary>A domain goes straight into JavaScript here, so anything that is not a domain does not go
    /// in at all. The list is ours today; a quote that slipped through a list the user can edit would
    /// break the PAC, and a broken PAC is a browser going DIRECT.</summary>
    internal static string Sanitise(string domain)
    {
        string name = domain.Trim().Trim('.').ToLowerInvariant();
        if (name.Length is 0 or > 253) return "";

        foreach (char c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.') return "";

        return name;
    }

    /// <summary>The PAC as Windows takes it: base64 inside the URL, so nothing has to serve it.</summary>
    internal static string PacUrl(string script) =>
        PacPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

    /// <summary>Bring Windows' proxy setting to the state the app wants — the whole machine through the
    /// proxy, a PAC for the routed names, both, or neither — and return the state to put back, encoded
    /// for <c>settings.json</c> (see <see cref="Restore"/>).
    ///
    /// <para><paramref name="heldBackup"/> is what was captured the first time. It is passed back in
    /// rather than re-read, because the second call — the user flipping one of the two switches while
    /// the proxy runs — would otherwise capture OUR values as if they were the user's, and the setting
    /// would never find its way home.</para>
    ///
    /// <para>Returns null when nothing was written: either the registry refused, or the PAC came out
    /// too long to be handed over safely.</para></summary>
    public static string? Apply(int port, bool systemWide, string pacScript, string heldBackup)
    {
        try
        {
            string wantedPac = pacScript.Length > 0 ? PacUrl(pacScript) : "";
            if (wantedPac.Length > PacUrlLimit) return null;

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                                    ?? throw new InvalidOperationException("Internet Settings unavailable");

            string ours = ProxyValue(port);
            string currentServer = key.GetValue("ProxyServer") as string ?? "";
            string currentOver = key.GetValue("ProxyOverride") as string ?? "";
            string currentPac = key.GetValue("AutoConfigURL") as string ?? "";
            bool currentEnabled =
                Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0, CultureInfo.InvariantCulture) != 0;

            bool serverIsOurs = Same(currentServer, ours);
            bool pacIsOurs = currentPac.StartsWith(PacPrefix, StringComparison.OrdinalIgnoreCase);

            string backup = heldBackup.Length > 0
                ? heldBackup
                : Encode(currentEnabled && !serverIsOurs,
                         serverIsOurs ? "" : currentServer,
                         serverIsOurs ? "" : currentOver,
                         pacIsOurs ? "" : currentPac);

            var (wasEnabled, wasServer, wasOver, wasPac) = Decode(backup);

            string server = systemWide ? ours : wasServer;
            string over = systemWide ? LocalBypass : wasOver;
            bool enabled = systemWide || (wasEnabled && wasServer.Length > 0);
            string pac = wantedPac.Length > 0 ? wantedPac : wasPac;

            if (Same(server, currentServer) && Same(over, currentOver)
                && enabled == currentEnabled && Same(pac, currentPac))
                return backup;                      // already exactly this — leave applications alone

            Write(key, "ProxyServer", server);
            Write(key, "ProxyOverride", over);
            key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            Write(key, "AutoConfigURL", pac);
            Notify();
            return backup;
        }
        catch { return null; }
    }

    /// <summary>Put back what <see cref="Apply"/> captured. Each half is put back only while it is still
    /// ours: the user (or another program) changing one of them while we ran means their answer is the
    /// newer one, and a PAC they set themselves must survive our shutdown.</summary>
    public static void Restore(string backup, int port)
    {
        if (backup.Length == 0) return;
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
                                    ?? throw new InvalidOperationException("Internet Settings unavailable");

            var (enabled, server, over, pac) = Decode(backup);

            string currentPac = key.GetValue("AutoConfigURL") as string ?? "";
            if (currentPac.StartsWith(PacPrefix, StringComparison.OrdinalIgnoreCase))
                Write(key, "AutoConfigURL", pac);

            string current = key.GetValue("ProxyServer") as string ?? "";
            if (current.Length == 0 || Same(current, ProxyValue(port)))
            {
                Write(key, "ProxyServer", server);
                Write(key, "ProxyOverride", over);
                key.SetValue("ProxyEnable", enabled ? 1 : 0, RegistryValueKind.DWord);
            }

            Notify();
        }
        catch { /* best-effort: a setting we cannot write back is not worth failing the shutdown over */ }
    }

    private static void Write(RegistryKey key, string name, string value)
    {
        if (value.Length > 0) key.SetValue(name, value, RegistryValueKind.String);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- the persisted backup -------------------------------------------------------------------

    /// <summary>"1|http=host:port|&lt;local&gt;|" — one readable line in settings.json rather than four
    /// fields, because it is recovery data and not a preference anyone edits. A three-field line from an
    /// older version still decodes: the PAC it did not know about comes back empty, which is what it
    /// was.</summary>
    internal static string Encode(bool enabled, string server, string over, string autoConfig) =>
        (enabled ? "1|" : "0|") + Field(server) + "|" + Field(over) + "|" + Field(autoConfig);

    private static string Field(string value) => value.Replace('|', ' ');

    internal static (bool Enabled, string Server, string Override, string AutoConfig) Decode(string backup)
    {
        string[] parts = backup.Split('|');
        return (parts.Length > 0 && parts[0] == "1",
                parts.Length > 1 ? parts[1] : "",
                parts.Length > 2 ? parts[2] : "",
                parts.Length > 3 ? parts[3] : "");
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
