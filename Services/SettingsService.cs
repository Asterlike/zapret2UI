using System.IO;
using System.Text.Json;

namespace ZapretUI.Services;

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
    /// lists (YouTube/Discord/Telegram) + your custom targets/hostlists are desynced — like Flowseal,
    /// so games/apps not in any list never break. When true, all other TLS/QUIC is desynced too
    /// (kept safe by the exclude list); convenient but may break a game/app that isn't excluded.</summary>
    public bool BypassAllSites { get; set; }
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
