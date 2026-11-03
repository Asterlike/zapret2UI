using System.IO;

namespace Zapret2UI.Services.Infrastructure;

/// <summary>
/// Central place for all on-disk locations. Two roots, split by who may write them:
///
/// <para><see cref="BinRoot"/> (<c>%ProgramFiles%\Zapret2UI</c>) holds everything that RUNS — winws2 and its
/// Lua (the Lua is code, executed inside winws2), usque, tor and the torrc that names the transport
/// plugin tor execs — plus the scratch folder downloads are verified and unpacked in. The app runs as
/// administrator and so does every child it starts; out of a folder the user can write, any program the
/// user runs could swap a file and be started with administrator's rights next time. Program Files
/// rather than ProgramData: an ordinary user may create folders in ProgramData and would own one created
/// before ours, while Program Files lets users only read — the right ACL with no code of ours.</para>
///
/// <para><see cref="Root"/> (<c>%LOCALAPPDATA%\Zapret2UI</c>) keeps the user's data: settings, presets,
/// lists, logs, the WARP device key, Tor's state.</para>
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zapret2UI");

    /// <summary>Executables and anything they execute. Writable only by administrators.</summary>
    public static string BinRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Zapret2UI");

    // One-time data-folder migration: the app was renamed ZapretUI → Zapret2UI. On first access (before
    // any service reads settings/presets), move an existing legacy folder over so users keep their
    // downloaded engine, settings and custom lists. Best-effort — a fresh folder if the move fails.
    static AppPaths()
    {
        try
        {
            string legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretUI");
            if (Directory.Exists(legacy) && !Directory.Exists(Root))
                Directory.Move(legacy, Root);
        }
        catch { /* keep going with a fresh folder */ }
    }

    // Engine (winws2.exe, WinDivert*, cygwin1.dll, lua\, files\)
    public static string EngineDir => Path.Combine(BinRoot, "engine");
    public static string EngineVersionFile => Path.Combine(EngineDir, "installed_version.txt");
    public static string WinwsExe => Path.Combine(EngineDir, "winws2.exe");
    public static string MdigExe => Path.Combine(EngineDir, "mdig.exe");
    public static string Ip2NetExe => Path.Combine(EngineDir, "ip2net.exe");
    public static string LuaDir => Path.Combine(EngineDir, "lua");
    public static string FilesDir => Path.Combine(EngineDir, "files");
    public static string WinDivertFilterDir => Path.Combine(EngineDir, "windivert.filter");

    /// <summary>Path of a named CIDR ipset, e.g. ipset("telegram") -> lists\ipset-telegram.txt.</summary>
    public static string IpsetFile(string name) => Path.Combine(ListsDir, $"ipset-{name}.txt");

    /// <summary>Aggregated CIDR ipset for Discord (built from the discord hostlist).</summary>
    public static string IpsetDiscordFile => IpsetFile("discord");

    /// <summary>Hostlist of the built-in Telegram proxy's Cloudflare fronting domains. The engine desyncs
    /// their TLS (via an appended profile) so the proxy's own upstream survives mobile DPI (TSPU) that
    /// kills the WebSocket tunnel mid-stream. Seeded by <see cref="HostlistService.SeedDefaults"/>.</summary>
    public static string TgProxyFrontsFile => Path.Combine(ListsDir, "tgproxy-fronts.txt");

    /// <summary>Hostlist of Cloudflare's WARP service domains. The engine desyncs their TLS via an
    /// always-appended profile, because registering a WARP device is an HTTPS request to
    /// <c>api.cloudflareclient.com</c> — and that name is cut by SNI on a censored network, so without
    /// this the WARP tab cannot get past its first step. Seeded by
    /// <see cref="HostlistService.SeedDefaults"/>.</summary>
    public static string WarpApiFile => Path.Combine(ListsDir, "warp-api.txt");

    /// <summary>WARP over MASQUE: the unpacked usque client and the device it registered.
    ///
    /// <para>This replaced a WireGuard implementation that could not work here at all — on a censored
    /// Russian network the handshake is let through and the transport stream is then cut, and no amount
    /// of desync repairs that. MASQUE is Cloudflare's own second transport and looks like ordinary
    /// HTTP/3 or HTTPS on 443.</para>
    ///
    /// <para>Split in two: the config holds a private key, so it stays under the user's profile; the
    /// client itself runs with administrator's rights like every child of this app, so it lives in
    /// <see cref="MasqueBinDir"/>.</para></summary>
    public static string MasqueDir => Path.Combine(Root, "masque");
    public static string MasqueBinDir => Path.Combine(BinRoot, "masque");
    public static string MasqueExe => Path.Combine(MasqueBinDir, "usque.exe");

    /// <summary>The registered MASQUE device: an ECDSA P-256 key plus the licence, id and access token
    /// Cloudflare hands back.</summary>
    public static string MasqueConfigFile => Path.Combine(MasqueDir, "config.json");

    /// <summary>Tor, when the user picks it as the way in to Cloudflare: the Expert Bundle unpacked on
    /// demand rather than carried inside our exe.
    ///
    /// <para>Downloaded rather than embedded on purpose. The three files that matter come to ~53 MB —
    /// a third of the whole application — and antivirus vendors treat a bundled tor.exe far more harshly
    /// than one the user asked for. The engine is downloaded for the same reasons, so this is the
    /// established shape rather than a new one.</para>
    ///
    /// <para>The torrc lives here too, not with the data: it names the program tor execs as its
    /// transport plugin, so whoever can edit it can have tor start anything with its rights.</para></summary>
    public static string TorDir => Path.Combine(BinRoot, "tor");
    public static string TorExe => Path.Combine(TorDir, "tor.exe");

    /// <summary>The pluggable transport (lyrebird) and the bundle's own list of built-in bridges.</summary>
    public static string TorTransportDir => Path.Combine(TorDir, "pt");
    public static string TorTransportExe => Path.Combine(TorTransportDir, "lyrebird.exe");
    public static string TorTransportConfig => Path.Combine(TorTransportDir, "pt_config.json");

    /// <summary>Country codes need these, and they are most of the bundle's size.</summary>
    public static string TorGeoIpFile => Path.Combine(TorDir, "geoip");
    public static string TorGeoIp6File => Path.Combine(TorDir, "geoip6");

    /// <summary>Tor's own state: the consensus it caches between runs, which is what makes the second
    /// start much faster than the first. State, not code — it stays in the profile.</summary>
    public static string TorDataDir => Path.Combine(Root, "tor", "data");

    /// <summary>Which bundle version is unpacked, so a pinned newer one replaces it.</summary>
    public static string TorVersionFile => Path.Combine(TorDir, "installed_version.txt");

    // User data
    public static string ListsDir => Path.Combine(Root, "lists");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string PresetsFile => Path.Combine(Root, "presets.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    // Scratch space for the user's own files (backups)
    public static string TempDir => Path.Combine(Root, "tmp");

    /// <summary>Scratch space for what becomes executable: the engine zip and its staging folder, the
    /// Tor archive, the logon task's XML (it names the program the task starts). In the profile, a file
    /// could be swapped between its checksum and its use.</summary>
    public static string BinTempDir => Path.Combine(BinRoot, "tmp");

    // Where the executables lived before they moved to BinRoot — read once to carry an engine over when
    // it cannot be downloaded, then deleted.
    public static string LegacyEngineDir => Path.Combine(Root, "engine");
    public static string LegacyMasqueExe => Path.Combine(MasqueDir, "usque.exe");
    public static string LegacyTorDir => Path.Combine(Root, "tor");

    /// <summary>The data folder from before the rename to Zapret2UI. The rename's move only runs when
    /// the new folder does not exist yet, so where both do, this one is a leftover.</summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretUI");

    /// <summary>Windows binary subfolder inside the release zip for this OS.</summary>
    public static string ReleaseArchFolder =>
        Environment.Is64BitOperatingSystem ? "windows-x86_64" : "windows-x86";

    /// <summary>The user-data folders. Never touches <see cref="BinRoot"/>: the crash log and the startup
    /// journal call this too, and in a process without administrator's rights (the screenshot harness)
    /// creating anything under Program Files throws.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ListsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TempDir);
    }

    /// <summary>The executables' side. Needs administrator's rights, which the app has.</summary>
    public static void EnsureBinCreated()
    {
        Directory.CreateDirectory(EngineDir);
        Directory.CreateDirectory(BinTempDir);
    }
}
