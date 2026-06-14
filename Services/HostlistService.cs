using System.IO;

namespace ZapretUI.Services;

/// <summary>
/// Manages domain hostlists stored as plain .txt files under the lists folder,
/// one domain per line — exactly the format winws2 expects for --hostlist.
/// </summary>
public sealed class HostlistService
{
    public HostlistService() => AppPaths.EnsureCreated();

    /// <summary>Names (without extension) of all available lists.</summary>
    public IReadOnlyList<string> GetLists()
    {
        try
        {
            return Directory.EnumerateFiles(AppPaths.ListsDir, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public string GetPath(string name) => Path.Combine(AppPaths.ListsDir, name + ".txt");

    public bool Exists(string name) => File.Exists(GetPath(name));

    public string Read(string name)
    {
        string p = GetPath(name);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    public List<string> ReadDomains(string name) =>
        Read(name)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    public void Write(string name, string content)
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(GetPath(name), NormalizeNewlines(content));
    }

    public void Create(string name)
    {
        if (!Exists(name)) Write(name, "");
    }

    public void Delete(string name)
    {
        string p = GetPath(name);
        if (File.Exists(p)) File.Delete(p);
    }

    public void AddDomain(string name, string domain)
    {
        domain = domain.Trim();
        if (domain.Length == 0) return;
        var domains = Exists(name) ? ReadDomains(name) : new List<string>();
        if (!domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            domains.Add(domain);
            Write(name, string.Join('\n', domains));
        }
    }

    /// <summary>Create a couple of useful starter lists on first run (if none exist yet).</summary>
    public void SeedDefaults()
    {
        if (GetLists().Count > 0) return;
        Write("youtube", string.Join('\n', new[]
        {
            "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com",
            "yt3.ggpht.com", "youtubei.googleapis.com", "jnn-pa.googleapis.com", "gstatic.com",
        }));
        // Full Discord domain set (Flowseal list-general.txt, ветка main, июнь 2026).
        // zapret сам матчит поддомены, поэтому достаточно базовых доменов.
        Write("discord", string.Join('\n', new[]
        {
            "discord.com", "discord.gg", "discord.media", "discord.app", "discord.co",
            "discord.dev", "discord.design", "discord.gift", "discord.gifts",
            "discord.new", "discord.store", "discord.status",
            "discordapp.com", "discordapp.net", "discordcdn.com", "discordstatus.com",
            "discordmerch.com", "discord-activities.com", "discordactivities.com",
            "discordsays.com", "discordsez.com", "discordpartygames.com",
            "discord-attachments-uploads-prd.storage.googleapis.com",
        }));
    }

    /// <summary>Validate a hostlist name so it cannot escape the lists folder.</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains("..");

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n');
}
