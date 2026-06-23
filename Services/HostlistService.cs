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
                // Custom-target machinery (managed on the Диагностика tab) — hide from the hostlist UI.
                .Where(n => !n.StartsWith("target-", StringComparison.OrdinalIgnoreCase)
                            && !n.Equals("targets", StringComparison.OrdinalIgnoreCase)
                            && !n.Equals("exclude-eff", StringComparison.OrdinalIgnoreCase))
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
    public static readonly string[] BundledListNames = { "youtube", "discord", "telegram", "exclude", "general" };

    /// <summary>Re-sync the bundled lists from code on EVERY launch, so domain updates reach existing
    /// installs (the user shouldn't be stuck on an old 4-host version). These are app-managed;
    /// user-created lists and the "proxy" list are never touched here.</summary>
    public void SeedDefaults()
    {
        Write("youtube", string.Join('\n', DefaultYoutube));
        Write("discord", string.Join('\n', DefaultDiscord));
        Write("telegram", string.Join('\n', DefaultTelegram));
        Write("exclude", string.Join('\n', DefaultExclude));
        Write("general", string.Join('\n', DefaultGeneral));
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
        // Cloudflare Turnstile widget — Discord's login bot-challenge loads from here
        // (challenges.cloudflare.com). In allow-list mode it wasn't desynced by anything, so the
        // challenge couldn't render → login stuck on net::ERR_CONNECTION_RESET. Ride the Discord desync.
        "challenges.cloudflare.com",
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

    /// <summary>General "everything else worth bypassing" domains (Flowseal list-general.txt, the
    /// non-Discord part): Cloudflare ECH/edge, Twitch ecosystem (BTTV/FFZ/7TV), CDNs. The catch-all
    /// profile already covers unknown SNIs, so this list is a reference users can attach explicitly.</summary>
    private static readonly string[] DefaultGeneral =
    {
        "cloudflare-ech.com", "encryptedsni.com", "cloudflareaccess.com", "cloudflareapps.com",
        "cloudflarebolt.com", "cloudflareclient.com", "cloudflareinsights.com", "cloudflareok.com",
        "cloudflarepartners.com", "cloudflareportal.com", "cloudflarepreview.com", "cloudflareresolve.com",
        "cloudflaressl.com", "cloudflarestatus.com", "cloudflarestorage.com", "cloudflarestream.com",
        "cloudflaretest.com", "cloudfront.net",
        "frankerfacez.com", "ffzap.com", "betterttv.net", "7tv.app", "7tv.io",
        "localizeapi.com", "klipy.com",
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
        // --- Game services (extended): the game filter is port-based, but these domains are also
        //     excluded so a catch-all desync never touches game logins/CDNs even with the filter ON.
        "steampowered.com", "steamstatic.com", "steamcontent.com", "steamusercontent.com",
        "steamserver.net", "valvesoftware.com", "steamgames.com",
        "ea.com", "eaassets-a.akamaihd.net", "origin.com", "dice.se",
        "battle.net", "battlenet.com.cn", "blizzard.com", "blz-contentstack.com",
        "ubisoft.com", "ubi.com", "ubisoftconnect.com",
        "rockstargames.com", "socialclub.rockstargames.com",
        "playstation.com", "playstation.net", "sonyentertainmentnetwork.com",
        "xbox.com", "nintendo.com", "nintendo.net", "nintendowifi.net",
        "gog.com", "gog-statics.com", "mojang.com",
        "wargaming.net", "faceit.com", "supercell.com",
        "hoyoverse.com", "mihoyo.com", "yuanshen.com",
        // --- Xbox / Microsoft sign-in (fixes "infinite Xbox login" e.g. in Forza). The token
        //     endpoints live under *.xboxlive.com / live.com / microsoftonline.com (already above),
        //     but the embedded sign-in web-view loads its assets from these CDNs — if the catch-all
        //     desyncs their TLS the login page never finishes and spins forever.
        "xboxservices.com", "msftauth.net", "msauth.net", "msftauthimages.net", "msauthimages.net",
        "microsoftonline-p.com", "s-microsoft.com", "login.windows.net", "gamepass.com",
        // --- Forza + Azure PlayFab (Forza Motorsport/Horizon backend & matchmaking).
        "forzamotorsport.net", "forzaracing.com", "playfab.com", "playfabapi.com",
        // --- Anti-cheat: mangled TLS here blocks game LAUNCH (not just login), so never desync.
        "easyanticheat.net", "eac-cdn.com", "battleye.com",
        // --- More launchers / publishers / online backends (auth, CDN, matchmaking) for the future.
        "fortnite.com", "activision.com", "callofduty.com", "demonware.net",
        "roblox.com", "rbxcdn.com", "2k.com", "take2games.com",
        "bethesda.net", "bethesda.com", "zenimax.com", "easports.com",
        "square-enix.com", "finalfantasyxiv.com", "bungie.net", "minecraftservices.com",
        "pubg.com", "krafton.com", "garena.com", "levelinfinite.com",
        "gaijin.net", "warthunder.com", "vkplay.ru", "hoyolab.com", "nvidiagrid.net",
        // --- Riot full set (LoL/TFT/LoR/Wild Rift): pvp.net is the League login/chat backend.
        "pvp.net", "teamfighttactics.com", "legendsofruneterra.com", "wildrift.com",
        // --- NetEase (Marvel Rivals, Naraka; easebar = NetEase anti-cheat) + Kuro (Wuthering Waves).
        "netease.com", "neteasegames.com", "easebar.com", "marvelrivals.com",
        "kurogames.com", "kurogame.com",
        // --- Other popular online games / publishers.
        "warframe.com", "digitalextremes.com", "pathofexile.com",
        "amazongames.com", "playlostark.com", "deadbydaylight.com", "bhvr.com",
        "escapefromtarkov.com", "battlestategames.com", "halowaypoint.com",
        "jagex.com", "arena.net", "guildwars2.com", "ncsoft.com",
        "nexon.com", "nexon.net", "pearlabyss.com", "embark-studios.com",
        "moonton.com", "mobilelegends.com",
        // --- Anti-cheat (mangled TLS blocks game launch): Denuvo, Wellbia (Xigncode3), GameGuard.
        "denuvo.com", "wellbia.com", "nprotect.com",
        // --- Third-party multiplayer backends / netcode: one platform powers MANY indie & AAA
        //     games (most don't run their own servers), so a single mangled host breaks multiplayer
        //     everywhere. EOS (epicgames.dev) + Azure PlayFab (playfab*) are already covered above.
        "photonengine.com", "exitgames.com",       // Photon — the biggest Unity/UE netcode SaaS
        "unity.com", "unity3d.com", "vivox.com",    // Unity Gaming Services + Vivox in-game voice
        "heroiclabs.com", "accelbyte.io",           // Nakama / AccelByte backends
        // --- Dedicated game-server hosting / orchestration (rented community servers).
        "nitrado.net", "gamefabric.com", "g-portal.com", "i3d.net", "edgegap.com",
    };

    /// <summary>Validate a hostlist name so it cannot escape the lists folder.</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains("..");

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n');
}
