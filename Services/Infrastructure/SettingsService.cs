using System.IO;
using System.Text.Json;

namespace Zapret2UI.Services.Infrastructure;

public sealed class AppSettings
{
    public string? ActivePresetName { get; set; }
    public string? ActiveHostlist { get; set; }
    public bool AutoUpdateEngine { get; set; } = true;
    public bool Autostart { get; set; }
    public bool AutostartEngine { get; set; }   // also start the engine on launch
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    /// <summary>Simple (one-click) vs Advanced (full tabs) interface. Simple by default.</summary>
    public bool SimpleMode { get; set; } = true;

    /// <summary>UI language: "ru" (default) or "en". Applied once at startup — the switch on
    /// Главная/Настройки is restart-to-apply (the XAML strings resolve at parse time). Only the
    /// interface is translated; engine arguments and lists are unaffected.</summary>
    public string Language { get; set; } = "ru";

    /// <summary>Background watchdog: silently re-pick a strategy if the bypass stops working.</summary>
    public bool AutoHeal { get; set; }

    /// <summary>Game filter (Flowseal-style): when true, the bypass capture is widened to all high
    /// ports (>1023) so throttled games get desynced too. When false (default), capture stays narrow
    /// (80,443 + Discord voice) so game traffic is left untouched and games run natively.</summary>
    public bool GameFilter { get; set; }

    /// <summary>Bypass EVERY site (catch-all) vs allow-list. When false (default), only the explicit
    /// lists (YouTube/Discord) + your custom targets/hostlists are desynced — like Flowseal,
    /// so games/apps not in any list never break. When true, all other TLS/QUIC is desynced too
    /// (kept safe by the exclude list); convenient but may break a game/app that isn't excluded.</summary>
    public bool BypassAllSites { get; set; }

    /// <summary>Drop the desynced services' QUIC (HTTP/3) so the browser falls back to TCP/H2. Turn on
    /// where the ISP/TSPU throttles or drops QUIC (YouTube stutters over HTTP/3 but is fine over TCP).</summary>
    public bool DisableQuic { get; set; }

    /// <summary>Also cover the built-in Telegram proxy's own Cloudflare upstream (443) with the DPI engine,
    /// so its tunnel survives mobile DPI (TSPU) that corrupts it mid-stream. Off by default — most users
    /// don't need it; turn on only if the proxy connects but keeps dropping. Needs the engine running.</summary>
    public bool TgProxyCoverage { get; set; }

    /// <summary>Local port the MASQUE proxy listens on. 1080 is the conventional SOCKS port; it is
    /// offered as a setting because another proxy sitting on it is common rather than exotic.</summary>
    public int MasqueListenPort { get; set; } = 1080;

    /// <summary>The transport that last carried traffic, so the next connection starts where the last one
    /// succeeded instead of walking the whole sweep again.
    ///
    /// <para>Defaults measured on a censored Russian ISP: HTTP/2 over TCP on 443 connected on every
    /// attempt with the bypass running, while QUIC failed on all seven ports. Cloudflare's own client
    /// calls this its «H3 with H2 fallback» — here the fallback is simply the one that works.</para></summary>
    public bool MasqueHttp2 { get; set; } = true;
    public int MasqueConnectPort { get; set; } = 443;

    /// <summary>Send the whole system through the WARP proxy while it runs, by pointing Windows' own
    /// proxy setting at it. Off by default: the proxy is opt-in per application by design, and this
    /// makes it opt-out instead — every browser tab, updater and game goes the long way round through
    /// Cloudflare. Firefox keeps its own proxy setting and is NOT covered.</summary>
    public bool MasqueSystemProxy { get; set; }

    /// <summary>Windows' proxy setting as it was before <see cref="MasqueSystemProxy"/> pointed it at
    /// the local proxy, encoded as <c>enabled|server|override</c>. Empty when nothing is applied.
    /// Persisted rather than held in memory precisely because it has to survive a crash: a registry
    /// setting left pointing at a proxy that is no longer listening is a machine with no browsing, and
    /// the next launch undoes it.</summary>
    public string SystemProxyBackup { get; set; } = "";

    /// <summary>Verbose engine log (<c>--debug=1</c>): winws2 reports per-connection decisions, which is
    /// what you need to see WHY a desync did or didn't fire. Off by default — it is noisy, and the
    /// Журнал buffer is capped, so ordinary startup messages scroll away faster while it is on.</summary>
    public bool DebugLog { get; set; }

    /// <summary>Per-network memory: a local network fingerprint (see <see cref="NetworkFingerprint"/>) →
    /// the last strategy that ran there. Lets the app re-suggest a known-good preset when you return to
    /// a network, instead of the generic default. Keyed locally; no external calls, no IPs stored.</summary>
    public Dictionary<string, string> NetworkStrategies { get; set; } = new();

    /// <summary>Local listen port for the built-in Telegram MTProto→WS proxy (TelegramProxyService).</summary>
    public int TgProxyPort { get; set; } = 1443;

    /// <summary>Persisted MTProto secret (32 hex chars) so the tg:// proxy link stays stable across
    /// runs. Empty on first run; filled in once the proxy is configured/started.</summary>
    public string TgProxySecret { get; set; } = "";

    /// <summary>Start the built-in Telegram proxy automatically on app launch.</summary>
    public bool TgProxyAutostart { get; set; }

    /// <summary>App-wide UI zoom (1.0–2.5), applied on TOP of the OS DPI scaling via a ScaleTransform.
    /// Lets the whole interface be enlarged on high-res/4K panels where Windows scaling is set low and
    /// everything looks tiny — independent of the system DPI. 1.0 = no extra zoom.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Show the app's own corner toast notifications (start/stop, auto-heal). Off = no popups.</summary>
    public bool NotificationsEnabled { get; set; } = true;

    /// <summary>Play a soft sound with each toast notification. Off = silent toasts.</summary>
    public bool NotificationSound { get; set; } = true;

    /// <summary>Collapse the donate/QR card to a compact button (persisted UI preference).</summary>
    public bool DonateCollapsed { get; set; }

    /// <summary>The first-run walkthrough has already been shown. Set when the user closes it; the
    /// walkthrough stays available from Настройки → «Показать вводную».</summary>
    public bool WelcomeShown { get; set; }
}

/// <summary>Loads/saves <see cref="AppSettings"/> as settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();

    public SettingsService() => Load();

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            // Temp-file + atomic replace: a crash mid-write can't corrupt settings.json (which Load
            // would then reject, resetting every setting to defaults).
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, JsonOpts));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    /// <summary>Reset every preference to its default, then save. A handful of identity/data fields are
    /// carried over rather than wiped: the interface <see cref="AppSettings.Language"/> and
    /// <see cref="AppSettings.SimpleMode"/> view mode (resetting them would yank the live UI), the current
    /// <see cref="AppSettings.ActivePresetName"/>/<see cref="AppSettings.ActiveHostlist"/> selection, the
    /// persisted <see cref="AppSettings.TgProxySecret"/> (so an already-configured tg:// link keeps
    /// working), the <see cref="AppSettings.NetworkStrategies"/> per-network memory and the
    /// <see cref="AppSettings.WelcomeShown"/> flag. The user's saved strategies live in presets.json and
    /// their host lists in lists\ — separate files this never touches.</summary>
    public void ResetToDefaults()
    {
        Settings = BuildReset(Settings);
        Save();
    }

    /// <summary>The reset result for a given current state: a fresh <see cref="AppSettings"/> with the
    /// identity/data fields carried over from <paramref name="current"/> and everything else at its
    /// default. Pure (no I/O) so the preserve-vs-reset split can be unit-tested.</summary>
    internal static AppSettings BuildReset(AppSettings current) => new()
    {
        Language = current.Language,
        SimpleMode = current.SimpleMode,
        ActivePresetName = current.ActivePresetName,
        ActiveHostlist = current.ActiveHostlist,
        TgProxySecret = current.TgProxySecret,
        NetworkStrategies = current.NetworkStrategies,
        WelcomeShown = current.WelcomeShown,
        // Not a preference: it is how Windows' own proxy setting gets put back. Wiping it on a reset
        // would strand the machine on a proxy that is about to stop listening.
        SystemProxyBackup = current.SystemProxyBackup,
    };

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                Settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch
        {
            Settings = new AppSettings();
            // Preserve the unreadable file instead of overwriting it on the next Save.
            try { File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bak", overwrite: true); } catch { }
        }
    }
}
