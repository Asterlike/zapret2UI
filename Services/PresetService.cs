using System.IO;
using System.Text.Json;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Provides the built-in (code-defined) strategies plus any user-created ones
/// persisted in presets.json. Built-ins are read-only starting points; users
/// duplicate them to customise.
/// </summary>
public sealed class PresetService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public List<Preset> UserPresets { get; private set; } = new();

    public PresetService() => Load();

    /// <summary>Built-ins first, then user presets.</summary>
    public IReadOnlyList<Preset> All => BuiltIns().Concat(UserPresets).ToList();

    public Preset? FindByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public void AddUser(Preset p)
    {
        p.IsBuiltIn = false;
        // Ensure a unique name.
        string baseName = p.Name;
        int i = 2;
        while (FindByName(p.Name) is not null)
            p.Name = $"{baseName} ({i++})";
        UserPresets.Add(p);
        Save();
    }

    public void UpdateUser(Preset p)
    {
        if (p.IsBuiltIn) return;
        Save();
    }

    public void DeleteUser(Preset p)
    {
        if (p.IsBuiltIn) return;
        UserPresets.Remove(p);
        Save();
    }

    /// <summary>Replace the auto-saved "top-3 of the last generation" leaderboard presets with a fresh
    /// set, so the saved trio and their scores stay current after each generation run. The incoming
    /// presets are flagged as leaderboard entries; the previous trio is dropped first.</summary>
    public void ReplaceAutoLeaderboard(IEnumerable<Preset> top)
    {
        UserPresets.RemoveAll(p => p.IsAutoLeaderboard);
        foreach (var p in top)
        {
            p.IsBuiltIn = false;
            p.IsAutoLeaderboard = true;
            UserPresets.Add(p);
        }
        Save();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            // Write to a temp file then atomically replace: a crash mid-write can't truncate the real
            // presets.json (which Load would then reject, wiping every user preset).
            string tmp = AppPaths.PresetsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(UserPresets, JsonOpts));
            File.Move(tmp, AppPaths.PresetsFile, overwrite: true);
        }
        catch { /* non-fatal */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(AppPaths.PresetsFile))
            {
                var list = JsonSerializer.Deserialize<List<Preset>>(
                    File.ReadAllText(AppPaths.PresetsFile));
                if (list is not null)
                {
                    foreach (var p in list) p.IsBuiltIn = false;
                    UserPresets = list;
                }
            }
        }
        catch
        {
            UserPresets = new();
            // Keep the unreadable file aside instead of letting the next Save overwrite it with an
            // empty list — the user can recover their presets from the .bak.
            try { File.Move(AppPaths.PresetsFile, AppPaths.PresetsFile + ".bak", overwrite: true); } catch { }
        }
    }

    // ---- built-in strategies ----------------------------------------------

    // All built-ins are SNI-routed "combo" strategies: they send Discord SNIs,
    // YouTube/Google SNIs and everything else through different TLS desyncs
    // (via the discord/youtube hostlists), and always carry the Discord voice
    // profile (discord+stun faked with a QUIC blob, the way Flowseal alt10 does it —
    // the server discards the QUIC garbage so there is no SSRC poison / NO_ROUTE, and
    // no fragile ttl cutting is needed). They differ only in the per-service
    // TLS desync "bundle". The generic bol-van presets were dropped — what works
    // is the *combination*, routed per service (see the nfqws1→nfqws2 migration
    // + Flowseal general.bat). DPI bypass is provider-specific; these are proven
    // starting points, not a guarantee for every ISP.

    // Recommended-combo TLS bundles, reused by the proxy presets so DC/YT stay identical to the base.
    private static readonly string[] RecDiscordTls = { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" };
    private static readonly string[] RecYoutubeTls =
    {
        "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
        "--lua-desync=multidisorder:pos=1,midsld",
    };

    public static List<Preset> BuiltIns() => new()
    {
        // 1) RECOMMENDED — the user-confirmed winners for this provider:
        //    Discord web/login/media = hostfakesplit, YouTube = fake+multidisorder.
        Combo("Комбо (рекомендуемый)",
            "Лучшее под каждый сервис в одной команде, маршрутизация по SNI. Discord → hostfakesplit " +
            "(быстрый логин + медиа), YouTube/Google → fake+multidisorder, остальное → hostfakesplit. " +
            "Голос: STUN + RTP-фикс. Не нужно переключаться между пресетами.",
            recommended: true,
            discordTls: RecDiscordTls, youtubeTls: RecYoutubeTls, fallbackTls: RecDiscordTls),

        // 2) TARGETED DOMESTIC — the domestic-fake counterpart to the recommended combo, and like it
        //    GATEWAY-SAFE: hostfakesplit splits by the SNI marker, so it adapts to the Discord native
        //    client's gateway ClientHello too (logs in AND connects to servers — no fixed-byte seqovl
        //    that a differently-sized gateway handshake would break). Fakes look like vk.com, which the
        //    TSPU whitelists, so it often breaks through where google-fakes get cut.
        Combo("Комбо — отечественный (VK, целевой)",
            "Целенаправленно под РФ: фейки для Discord маскируются под vk.com (ТСПУ не роняет отечественный " +
            "трафик). Как и рекомендуемый — hostfakesplit по маркеру SNI, поэтому пускает и в логин, и к " +
            "серверам (гейтвею), а не только на страницу входа. Discord/остальное → hostfakesplit vk.com, " +
            "YouTube → fake(google)+multidisorder (vk-фейк для googlevideo не годится — там нужен обычный " +
            "рабочий профиль). Голос: STUN + RTP-фикс. Первый выбор, если google-варианты «зелёные, но не " +
            "открывают» Discord.",
            recommended: false,
            discordTls: new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" },
            youtubeTls: RecYoutubeTls,
            fallbackTls: new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" }),

        // 3) Flowseal general (ALT10), переведён на nfqws2. У сообщества «работает вообще всё»: ЧИСТЫЙ
        //    fake (без сплита) с ts-fooling и ДВОЙНЫМ фейком (google + отечественный vk-ClientHello),
        //    а голос — доменным QUIC-блобом (vk) вместо google. Это чинит «войс отваливается / медиа не
        //    грузится», когда сплит-подходы пускают только в логин. fooling=ts → tcp_ts=-1000.
        Combo("Комбо — Flowseal ALT10 (двойной fake + ts)",
            "Перевод Flowseal general (ALT10) на nfqws2: без разрезки — двойной fake-пакет (google + " +
            "vk-ClientHello) с ts-fooling, голос через отечественный QUIC-блоб (vk). Часто пробивает и " +
            "гейтвей, и медиа, и голос там, где сплит-стратегии дают только логин. Первый выбор при " +
            "«залогинился, но не подключает / войс молчит».",
            recommended: false,
            discordTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" },
            youtubeTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:ip_id=zero:repeats=6" },
            fallbackTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                 "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" },
            voiceDesync: new[] { "--lua-desync=fake:blob=quic_vk:repeats=6" }),

        // 4) Flowseal general (ALT11), переведён на nfqws2: fake-прайм с ts + multisplit с большим seqovl
        //    (681/664) и реальным google-ClientHello как паттерном. Голос — доменным QUIC-блобом (vk).
        //    ВАЖНО: seqovl тут «безопасен для гейтвея» именно потому, что ему предшествует fake:ts-прайм.
        Combo("Комбо — Flowseal ALT11 (fake+ts → seqovl)",
            "Перевод Flowseal general (ALT11) на nfqws2: fake-пакет с ts-fooling + multisplit с большим " +
            "seqovl (реальный google-ClientHello как паттерн), голос через отечественный QUIC-блоб (vk). " +
            "Если ALT10 не зашёл — пробуйте этот.",
            recommended: false,
            discordTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:optional" },
            youtubeTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:ip_id=zero:optional" },
            fallbackTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                 "--lua-desync=multisplit:pos=1,midsld:seqovl=664:seqovl_pattern=tls_google:optional" },
            voiceDesync: new[] { "--lua-desync=fake:blob=quic_vk:repeats=6" }),

        // NB: Telegram is intentionally NOT covered by any preset — the native built-in tg-ws-proxy
        // (TelegramProxyService, started from the Telegram card) handles it instead of a DPI desync.

        // 2) Flowseal general.bat (June 2026), translated 1:1: multisplit + big seqovl
        //    with a real google ClientHello as the overlap pattern, per hostlist.
        Combo("Комбо — Flowseal (multisplit seqovl)",
            "Актуальная стратегия Flowseal general, переведённая на nfqws2: multisplit с большим seqovl " +
            "и реальным ClientHello google как паттерном, раздельно по SNI. Голос: STUN + RTP-фикс.",
            recommended: false,
            discordTls: new[] { "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:optional" },
            youtubeTls: new[] { "--lua-desync=multisplit:pos=2:seqovl=681:seqovl_pattern=tls_google:ip_id=zero:optional" },
            fallbackTls: new[] { "--lua-desync=multisplit:pos=2:seqovl=568:seqovl_pattern=tls_google:optional" }),

        // 3) Flowseal general (ALT).bat, translated: fake (ts) + fakedsplit (fooling on fakes).
        Combo("Комбо — Flowseal ALT (fake+fakedsplit)",
            "Альтернатива Flowseal ALT: fake с tcp_ts + fakedsplit (fooling только на фейках), раздельно " +
            "по SNI. Голос: STUN + RTP-фикс. Пробуйте, если multisplit-вариант не пробивает.",
            recommended: false,
            discordTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                "--lua-desync=fakedsplit:tcp_ts=-1000" },
            youtubeTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                "--lua-desync=fakedsplit:tcp_ts=-1000" },
            fallbackTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                                 "--lua-desync=fakedsplit:tcp_ts=-1000" }),

        // 4) wssize bundle: split+seqovl then force the server to fragment its reply.
        Combo("Комбо — окно (wssize)",
            "Для упрямого блока входа: разрезка ClientHello (реальный паттерн) + wssize заставляет " +
            "сервер дробить ответ. YouTube → fake+multidisorder. Голос: STUN + RTP-фикс. Медленнее, но " +
            "иногда пробивает там, где остальное нет.",
            recommended: false,
            discordTls: new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                                "--lua-desync=wssize:wsize=1:scale=6" },
            youtubeTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
                                "--lua-desync=multidisorder:pos=1,midsld" },
            fallbackTls: new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                                 "--lua-desync=wssize:wsize=1:scale=6" }),

        // 5) Standalone voice — minimal, for testing voice in isolation.
        new Preset
        {
            Name = "Discord — голос (QUIC-фейк)",
            Description = "Только починка голоса (без разбивки TLS по SNI). discord+stun на голосовых портах " +
                          "фейкуются QUIC-блобом google — сервер отбрасывает его как мусор, поэтому SSRC не " +
                          "портится и нет NO_ROUTE. Для случая «подключается, но никого не слышно / пинг 5000 / " +
                          "Connection timed out». Без ограничения ttl (рабочий путь Flowseal alt10).",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-tcp-out=80,443-65535",
                "--wf-udp-out=443-65535",
                "--ctrack-disable=0",
                "--ipcache-lifetime=8400",
                "--ipcache-hostname=1",
                "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
                "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
                // discord.media control socket (TLS) — лёгкая разрезка, чтобы пускало в войс. Скоупим
                // строго по discord (не catch-all), иначе профиль выпиливается в режиме «только списки».
                "--filter-tcp=443-65535", "--filter-l7=tls", "{HOSTLIST:discord}", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                "--new",
                // Голос: discord+stun на ВСЁМ диапазоне 50000-65535 → фейк QUIC-блобом, без ttl-ограничения
                // (route-independent альтернатива анти-дропу из комбо — пробуйте, если autottl не зашёл).
                "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun",
                  "--lua-desync=fake:blob=quic_google:repeats=6",
            }
        },

        // 6) Standalone ADAPTIVE Discord (circular). Экспериментальный: движковый оркестратор circular
        //    сам переключает стратегию десинка, когда его детектор ловит провал (RST / ретрансмиссия /
        //    DPI-редирект) — ровно случай «залогинился, но не подключает 20 сек / войс отваливается».
        //    Три ступени = три НЕЗАВИСИМО рабочих Discord-профиля: strat1 hostfakesplit (gateway-safe,
        //    быстрый вход) → strat2 ALT10 (двойной fake google+vk) → strat3 ALT11 (fake:ts прайм +
        //    seqovl). Без 'final' → крутит 1→2→3 по кругу, пока success-детектор не увидит рабочую;
        //    успешную больше не трогает (движок не перезапускается — переключение на лету, по хосту).
        //    circular ТРЕБУЕТ --in-range (кэш входящих RST для детектора) — он тут строго в Discord-TLS
        //    профиле. Только Discord+голос: YouTube не трогает, запускать точечно под упрямый Discord.
        new Preset
        {
            Name = "Discord — адаптивный (circular, эксперим.)",
            Description = "Самоподстраивающийся пресет ТОЛЬКО под Discord: circular сам переключает " +
                          "стратегию (hostfakesplit → двойной fake → seqovl), когда ловит RST/ретрансмиссию " +
                          "от DPI. Ровно для «вход проходит, но не подключает / войс молчит». Крутит варианты " +
                          "на лету (без перезапуска движка) и оседает на первом рабочем — дайте ему несколько " +
                          "секунд после старта. YouTube не трогает; запускайте, когда упрямится именно Discord.",
            IsBuiltIn = true,
            Args = new()
            {
                "{WF_TCP}",
                "{WF_UDP}",
                "--ctrack-disable=0",
                "--ipcache-lifetime=8400",
                "--ipcache-hostname=1",
                "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
                "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
                "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
                "--blob=quic_vk:@{FILES}\\fake\\quic_initial_vk_com.bin",
                "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                // 1) Discord web/login/gateway/media (TLS, по SNI) с адаптивной ротацией.
                //    --in-range ОБЯЗАТЕЛЕН для circular: без кэша входящих RST детектор провала не сработает.
                "--filter-tcp=443-65535", "--filter-l7=tls", "{HOSTLIST:discord}",
                  "--in-range=-s5556", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=circular:fails=2:time=300",
                  // strat 1 — hostfakesplit (gateway-safe, адаптируется к размеру ClientHello гейтвея).
                  "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4:strategy=1",
                  // strat 2 — ALT10: двойной fake (google + отечественный vk), без сплита.
                  "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6:strategy=2",
                  "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6:strategy=2",
                  // strat 3 — ALT11: fake:ts прайм + multisplit c большим seqovl (реальный google-CH).
                  //    :optional — если разрезка не применилась, тихо пропустить, а не ронять оркестрацию.
                  "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6:strategy=3",
                  "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:strategy=3:optional",
                "--new",
                // 2) QUIC Discord (медиа/вложения cdn/media.discordapp по HTTP/3) → google-фейк.
                "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:discord}", "--payload=quic_initial",
                  "--lua-desync=fake:blob=quic_google:repeats=11",
                "--new",
                // 3) Голос Discord (STUN + RTP, весь высокий диапазон) → отечественный QUIC-блоб (как в ALT10/11).
                "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun",
                  "--lua-desync=fake:blob=quic_vk:repeats=6",
            }
        },

    };

    /// <summary>
    /// Builds a full SNI-routed combo preset: per-service TLS desyncs (Discord /
    /// YouTube / fallback) + QUIC + Discord voice (discord+stun QUIC-blob fake).
    /// Only the three TLS bundles differ between built-ins; everything else is shared.
    /// </summary>
    private static Preset Combo(
        string name, string description, bool recommended,
        string[] discordTls, string[] youtubeTls, string[] fallbackTls,
        string discordFilter = "{HOSTLIST:discord}", string[]? voiceDesync = null)
        => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsRecommended = recommended,
            Args = BuildComboArgs(discordTls, youtubeTls, fallbackTls, discordFilter, voiceDesync),
        };

    /// <summary>Build the shared combo argument list (per-service TLS bundles + QUIC + Discord voice).
    /// Reused by the strategy generator to assemble a personal preset from generated TLS bundles.</summary>
    public static List<string> BuildComboArgs(
        string[] discordTls, string[] youtubeTls, string[] fallbackTls,
        string discordFilter = "{HOSTLIST:discord}", string[]? voiceDesync = null)
    {
        var a = new List<string>
        {
            "{WF_TCP}",
            "{WF_UDP}",
            "--ctrack-disable=0",
            "--ipcache-lifetime=8400",
            "--ipcache-hostname=1",
            "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
            "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
            "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
            // Отечественные фейк-блобы (ClientHello vk.com / sberbank.ru) — ТСПУ вайтлистит домашний
            // трафик, поэтому фейк «под vk/сбер» выживает там, где google-фейк режется (тренд 2026:
            // sonicdpi → ozon.ru, Flowseal → dbankcloud_ru). Входят в стандартную поставку движка.
            "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
            "--blob=tls_sber:@{FILES}\\fake\\tls_clienthello_sberbank_ru.bin",
            "--blob=tls_gos:@{FILES}\\fake\\tls_clienthello_gosuslugi_ru.bin",
            // Отечественный QUIC-блоб для голоса Discord (аналог dbankcloud_ru из Flowseal ALT10/11).
            "--blob=quic_vk:@{FILES}\\fake\\quic_initial_vk_com.bin",
            "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
            "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
            // 1) Discord web/login/media (по SNI).
            "--filter-tcp=443-65535", "--filter-l7=tls", discordFilter, "--out-range=-d10", "--payload=tls_client_hello",
        };
        a.AddRange(discordTls);
        // 2) YouTube / Google (по SNI).
        a.Add("--new");
        a.AddRange(new[] { "--filter-tcp=443-65535", "--filter-l7=tls", "{HOSTLIST:youtube}", "--out-range=-d10", "--payload=tls_client_hello" });
        a.AddRange(youtubeTls);
        // NB: Telegram is intentionally NOT handled by the combo — the built-in tg-ws-proxy
        // (TelegramProxyService) carries Telegram over WebSocket instead of a DPI desync profile.
        // 3) Остальной TLS (вкл. hCaptcha и пр.). Catch-all — поэтому исключаем чувствительные
        //    домены (банки/госуслуги/VK/Яндекс/Steam/…) через {EXCLUDE:exclude}, чтобы их не сломать.
        a.Add("--new");
        a.AddRange(new[] { "--filter-tcp=443-65535", "--filter-l7=tls", "{EXCLUDE:exclude}", "--out-range=-d10", "--payload=tls_client_hello" });
        a.AddRange(fallbackTls);
        // 4) QUIC YouTube (по SNI) → google-фейк.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:youtube}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=quic_google:repeats=11" });
        // 4b) QUIC Discord (по SNI) → google-фейк. cdn/media.discordapp по HTTP/3: без отдельного
        //     профиля медиа/вложения не грузятся в режиме «только списки» (там catch-all QUIC отключён).
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:discord}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=quic_google:repeats=11" });
        // 5) QUIC остальное → дефолтный фейк. Тоже catch-all → исключаем чувствительные домены.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{EXCLUDE:exclude}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=fake_default_quic:repeats=6" });
        // 6) Голос Discord (STUN + IP-discovery). Порты войса — ВЕСЬ высокий диапазон 50000-65535
        //    (узкий 50000-50100 пропускал половину войс-серверов → вечный 5000 пинг). Фейк QUIC-блобом
        //    google (мусор для войс-потока → SSRC не портится) + ip_autottl: фейк умирает на DPI
        //    провайдера, не доходя до сервера (анти-дроп), поэтому реальный RTP идёт без троттлинга.
        //    repeats=2 — лёгкий, как в актуальном фиксе 5к-пинга (Flowseal #12614 «Anti-Drop»).
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun" });
        // Default voice: google QUIC-blob junk + ip_autottl anti-drop (Flowseal #12614). ALT10/11 pass a
        // domestic-blob voice (quic_vk) instead — that's what breaks 5k-ping/«не слышно» on some nets.
        a.AddRange(voiceDesync ?? new[]
            { "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2" });

        return a;
    }

}
