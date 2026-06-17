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
                // ipset-*.txt are resolved IP sets, not domain hostlists — hide them from the list UI.
                .Where(n => !n.StartsWith("ipset-", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>The bundled "authored" lists, kept in sync with the code below.</summary>
    public static readonly string[] BundledListNames = { "youtube", "discord", "telegram", "exclude" };

    /// <summary>Re-sync the bundled lists from code on EVERY launch, so domain updates reach existing
    /// installs (the user shouldn't be stuck on an old 4-host version). These four are app-managed;
    /// user-created lists and the "proxy" list are never touched here.</summary>
    public void SeedDefaults()
    {
        Write("youtube", string.Join('\n', DefaultYoutube));
        Write("discord", string.Join('\n', DefaultDiscord));
        Write("telegram", string.Join('\n', DefaultTelegram));
        Write("exclude", string.Join('\n', DefaultExclude));
    }

    // ---- bundled default lists (synced from Flowseal/zapret-discord-youtube, июнь 2026) ----

    /// <summary>YouTube/Google domains (Flowseal list-google.txt).</summary>
    private static readonly string[] DefaultYoutube =
    {
        "yt3.ggpht.com", "yt4.ggpht.com", "yt3.googleusercontent.com", "googlevideo.com",
        "jnn-pa.googleapis.com", "stable.dl2.discordapp.net", "wide-youtube.l.google.com",
        "youtube-nocookie.com", "youtube-ui.l.google.com", "youtube.com",
        "youtubeembeddedplayer.googleapis.com", "youtubekids.com", "youtube.googleapis.com",
        "youtubei.googleapis.com", "youtu.be", "yt-video-upload.l.google.com", "ytimg.com",
        "ytimg.l.google.com", "play.google.com", "google.ru",
    };

    /// <summary>Full Discord domain set (Flowseal list-general.txt, Discord entries).
    /// zapret matches subdomains, so the base domains are enough.</summary>
    private static readonly string[] DefaultDiscord =
    {
        "dis.gd", "discord.com", "discord.gg", "discord.media", "discord.app", "discord.co",
        "discord.dev", "discord.design", "discord.gift", "discord.gifts",
        "discord.new", "discord.store", "discord.status",
        "discordapp.com", "discordapp.net", "discordcdn.com", "discordstatus.com",
        "discordmerch.com", "discord-activities.com", "discordactivities.com",
        "discordsays.com", "discordsez.com", "discordpartygames.com",
        "discord-attachments-uploads-prd.storage.googleapis.com",
    };

    /// <summary>Telegram-owned SNI domains (curated). zapret matches subdomains, so apex covers
    /// all *.telegram.org. NOTE: this only helps the SNI/web parts (web client, site, login,
    /// telegra.ph, HTTPS CDN). The desktop/mobile app's media goes over MTProto to DC IPs (no SNI)
    /// — that path is handled by the telegram ipset, see IpsetService.BuildTelegramIpsetAsync.</summary>
    private static readonly string[] DefaultTelegram =
    {
        "telegram.org", "t.me", "telegram.me", "tx.me", "teleg.xyz",
        "telegra.ph", "graph.org", "telesco.pe", "comments.app",
        "fragment.com", "contest.com", "quiz.directory",
        "tg.dev", "tg.org", "tgram.org", "torg.org", "telegramapp.org",
        "cdn-telegram.org", "telegram-cdn.org", "tdesktop.com",
        "telegram.space", "telega.one",
    };

    /// <summary>Domains that must NEVER be desynced (Flowseal list-exclude.txt) — banks, gov,
    /// big RU services, Microsoft/Steam/Riot/Epic, etc. Wired into catch-all profiles via
    /// --hostlist-exclude so a broad fallback strategy can't break them.</summary>
    private static readonly string[] DefaultExclude =
    {
        "pusher.com", "live-video.net", "ttvnw.net", "twitch.tv", "mail.ru", "citilink.ru",
        "yandex.com", "yandex.net", "yandex.org", "yandex.md", "yandex.ru", "yandexadexchange.net",
        "yandexcloud.net", "yandexcom.net", "yandexmetrica.com", "yandexwebcache.net",
        "yandexwebcache.org", "yastat.net", "yastatic-net.ru", "yastatic.net", "ya.ru",
        "adfox.ru", "admetrica.ru", "naydex.net", "rostaxi.org", "turbopages.org", "webvisor.com",
        "webvisor.org", "nvidia.com", "donationalerts.com", "vk.com", "yandex.kz", "mts.ru",
        "multimc.org", "dns-shop.ru", "habr.com", "3dnews.ru", "microsoft.com", "microsoftonline.com",
        "live.com", "sharepoint.com", "minecraft.net", "xboxlive.com", "akamaitechnologies.com",
        "msi.com", "2ip.ru", "boosty.to", "tanki.su", "lesta.ru", "korabli.su", "tanksblitz.ru",
        "reg.ru", "epicgames.dev", "epicgames.com", "unrealengine.com", "riotgames.com", "riotcdn.net",
        "leagueoflegends.com", "playvalorant.com", "marketplace.visualstudio.com", "gallery.vsassets.io",
        "gallerycdn.vsassets.io", "gosuslugi.ru", "gov.ru", "nalog.ru", "spb.ru", "mos.ru", "vk.ru",
        "vk.me", "vkvideo.ru", "ok.ru", "mycdn.me", "okcdn.ru", "odkl.ru", "wb.ru", "geobasket.ru",
        "paywb.com", "rwb.ru", "wb-basket.ru", "wbbasket.ru", "wbpay.ru", "wibes.ru", "wildberries.ru",
        "ozon.by", "ozon.com", "ozon.com.by", "ozon.com.kz", "ozon.kz", "ozon.ru", "ozon.tm",
        "ozone.ru", "ozonru.me", "ozonusercontent.com", "alfabank.ru", "gazprombank.ru", "gpb.ru",
        "dbo-dengi.online", "mtsdengi.ru", "psbank.ru", "bankline.ru", "rosbank.ru", "abr.ru",
        "rshb.ru", "sber.ru", "sberbank.com", "sberbank.ru", "cdn-tinkoff.ru", "tbank-online.com",
        "tbank.ru", "t-bank-app.ru", "tochka-tech.com", "tochka.com", "vtb.ru", "steamcommunity.com",
    };

    /// <summary>Validate a hostlist name so it cannot escape the lists folder.</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains("..");

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n');
}
