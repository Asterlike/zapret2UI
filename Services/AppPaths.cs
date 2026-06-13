using System.IO;

namespace ZapretUI.Services;

/// <summary>
/// Central place for all on-disk locations. Everything lives under
/// %LOCALAPPDATA%\ZapretUI so the app never needs to write to Program Files.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZapretUI");

    // Engine (winws2.exe, WinDivert*, cygwin1.dll, lua\, files\)
    public static string EngineDir => Path.Combine(Root, "engine");
    public static string EngineVersionFile => Path.Combine(EngineDir, "installed_version.txt");
    public static string WinwsExe => Path.Combine(EngineDir, "winws2.exe");
    public static string LuaDir => Path.Combine(EngineDir, "lua");
    public static string FilesDir => Path.Combine(EngineDir, "files");

    // User data
    public static string ListsDir => Path.Combine(Root, "lists");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string PresetsFile => Path.Combine(Root, "presets.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    // Scratch space for downloads
    public static string TempDir => Path.Combine(Root, "tmp");

    /// <summary>Windows binary subfolder inside the release zip for this OS.</summary>
    public static string ReleaseArchFolder =>
        Environment.Is64BitOperatingSystem ? "windows-x86_64" : "windows-x86";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(EngineDir);
        Directory.CreateDirectory(ListsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(TempDir);
    }
}
