using System.IO;
using System.Text.Json;

namespace Zapret2UI.Services;

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
