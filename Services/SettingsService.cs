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
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Settings, JsonOpts));
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
        catch { Settings = new AppSettings(); }
    }
}
