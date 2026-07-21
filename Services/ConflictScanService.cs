using System.Diagnostics;
using System.Net.NetworkInformation;

namespace Zapret2UI.Services;

/// <summary>How badly a finding affects the bypass.</summary>
public enum EnvSeverity
{
    /// <summary>Hard conflict — the bypass will not work until it's resolved.</summary>
    Conflict,
    /// <summary>Likely to break the bypass, but not guaranteed.</summary>
    Warning,
    /// <summary>Nothing wrong (used for the "all clear" card).</summary>
    Ok,
}

/// <summary>
/// One environment-check result. <paramref name="Title"/> says WHAT was found, <paramref name="Detail"/>
/// why it matters, and <paramref name="Action"/> the concrete steps to fix it — the point is that the
/// user can act on it without asking anyone. <paramref name="Action"/> is empty on the "all clear" card.
/// </summary>
public sealed record EnvFinding(EnvSeverity Severity, string Title, string Detail, string Action);

/// <summary>
/// Environment check for things that commonly stop the DPI-bypass engine from working:
///   • another running DPI-bypass tool — they all grab the WinDivert driver, and two filters on the
///     same packets conflict;
///   • an active VPN client or tunnel adapter — it re-routes traffic, so the desync never reaches the
///     provider and the bypass looks "broken";
///   • (manual check only) a missing or half-installed engine.
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

    // Explanations live here so every finding of the same kind reads identically.
    private const string DpiToolDetail =
        "Эта программа уже держит драйвер WinDivert. Два обхода не могут править одни и те же пакеты: " +
        "движок либо не запустится совсем, либо будет срабатывать через раз — и стратегия, которая на " +
        "самом деле рабочая, покажется нерабочей.";
    private const string DpiToolAction =
        "Закройте её полностью — не только окно, но и значок рядом с часами (правой кнопкой → выход). " +
        "Если она прописана в автозапуске, отключите его, иначе она вернётся после перезагрузки. " +
        "Затем включите обход здесь.";

    private const string VpnDetail =
        "VPN заворачивает весь трафик в свой туннель. Движок правит пакеты уже после этого, и до " +
        "провайдера они доходят запечатанными внутри туннеля — обходить становится нечего, поэтому обход " +
        "выглядит сломанным, даже если стратегия верная.";
    private const string VpnAction =
        "Отключитесь от VPN, пока пользуетесь обходом. Обход решает ту же задачу, что и VPN, — вместе " +
        "они не нужны. Если VPN обязателен для работы, включайте их по очереди, а не одновременно.";

    private const string AdapterDetail =
        "Сетевой адаптер туннеля включён. Такое бывает и при закрытом VPN-клиенте: адаптер остаётся в " +
        "системе и продолжает уводить часть трафика мимо движка.";
    private const string AdapterAction =
        "Выйдите из VPN-клиента полностью. Если адаптер остался от уже удалённой программы, отключите " +
        "его: Параметры Windows → Сеть и Интернет → Дополнительные сетевые параметры → выбрать адаптер → «Отключить».";

    /// <summary>
    /// Conflicts only (running software + tunnel adapters). This is the startup advisory: it runs
    /// before the engine auto-download, so engine state is deliberately NOT checked here — on a first
    /// launch the engine is legitimately absent for a few seconds.
    /// </summary>
    public static List<EnvFinding> ScanConflicts()
    {
        var findings = new List<EnvFinding>();

        try
        {
            var running = Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch { return ""; } finally { p.Dispose(); } })
                .Where(n => n.Length > 0)
                .Select(n => n.ToLowerInvariant())
                .ToHashSet();

            foreach (var (proc, label) in DpiTools)
                if (running.Contains(proc))
                    findings.Add(new EnvFinding(EnvSeverity.Conflict, label, DpiToolDetail, DpiToolAction));

            var seenVpn = new HashSet<string>();
            foreach (var (proc, label) in VpnApps)
                if (running.Contains(proc) && seenVpn.Add(label))
                    findings.Add(new EnvFinding(EnvSeverity.Warning, "VPN: " + label, VpnDetail, VpnAction));
        }
        catch { /* best-effort — a failed scan just warns about nothing */ }

        try
        {
            foreach (var name in VpnAdapters())
                findings.Add(new EnvFinding(
                    EnvSeverity.Warning, "Активен VPN-адаптер: " + name, AdapterDetail, AdapterAction));
        }
        catch { /* ignore adapter enumeration failures */ }

        return findings;
    }

    /// <summary>
    /// Full check for the manual "Проверить окружение" button: everything <see cref="ScanConflicts"/>
    /// finds, plus the state of the engine itself. Safe to include the engine here because the user
    /// runs this after startup has settled.
    /// </summary>
    public static List<EnvFinding> ScanEnvironment(bool engineInstalled, bool engineComplete)
    {
        var findings = ScanConflicts();

        if (!engineInstalled)
            findings.Add(new EnvFinding(EnvSeverity.Conflict,
                "Движок не установлен",
                "winws2 — это то, что собственно обходит блокировку; без него кнопка «Включить обход» " +
                "работать не будет. Обычно он скачивается сам при первом запуске, так что если его до сих " +
                "пор нет — загрузка не прошла.",
                "Откройте «Настройки» → «Проверить обновления» и дождитесь окончания загрузки. Если она " +
                "падает с ошибкой сети, дело в блокировке githubusercontent.com у вашего провайдера: " +
                "приложение уже пробует обойти её через DoH, но надёжнее всего включить VPN на время " +
                "одной этой загрузки, а потом выключить."));
        else if (!engineComplete)
            findings.Add(new EnvFinding(EnvSeverity.Warning,
                "Движок установлен не полностью",
                "Сам winws2 на месте, но часть файлов отсутствует — обычно это набор фильтров WinDivert, " +
                "который появился в более новых версиях. Стратегии, которые их используют, не запустятся.",
                "«Настройки» → «Проверить обновления». Недостающие файлы докачаются поверх текущей версии, " +
                "переустанавливать и удалять ничего не нужно."));

        return findings;
    }

    // Active tunnel/VPN network adapters (WireGuard/OpenVPN-TAP/WARP…), excluding Windows' own IPv6
    // transition pseudo-tunnels so they don't raise a false alarm. Yields the adapter name.
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
            if (isVpn) yield return ni.Name;
        }
    }
}
