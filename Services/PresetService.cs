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

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(AppPaths.PresetsFile, JsonSerializer.Serialize(UserPresets, JsonOpts));
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
        catch { UserPresets = new(); }
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

        // 1b/1c/1d) Telegram via YOUR MTProxy (secret ee). Same as the recommended combo (DC+YT+voice)
        //     plus ONE extra profile desyncing ONLY the proxy server's IP ({IPSET:proxy}). The three
        //     differ only in that proxy desync — on different ISPs/TSPU a different one connects, so the
        //     user tries them. Requires entering the proxy host first (RequiresProxyHost gates connect).
        Combo("Комбо + Telegram через ваш прокси (осн.)",
            "Рекомендуемое комбо + профиль под ВАШ MTProxy (секрет ee). Десинк применяется ТОЛЬКО к IP " +
            "вашего прокси-сервера — YouTube/Discord/голос как в основном комбо. Основной вариант " +
            "(лучший в тестах). Сначала укажите хост прокси, иначе подключение недоступно. На разных " +
            "операторах связи может зайти «вариант 2/3» — если этот нестабилен, пробуйте их.",
            recommended: false,
            discordTls: RecDiscordTls, youtubeTls: RecYoutubeTls, fallbackTls: RecDiscordTls,
            proxyTls: new[] { "--lua-desync=multidisorder:pos=88,176,264,352,440" }),

        Combo("Комбо + Telegram через ваш прокси (вариант 2)",
            "То же, но другой десинк прокси (нарезка по маркерам имени). На части операторов связи " +
            "заходит лучше основного, на части — хуже. Пробуйте, если основной нестабилен. " +
            "Сначала укажите хост прокси.",
            recommended: false,
            discordTls: RecDiscordTls, youtubeTls: RecYoutubeTls, fallbackTls: RecDiscordTls,
            proxyTls: new[] { "--lua-desync=multidisorder:pos=1,sniext,midsld,endhost" }),

        Combo("Комбо + Telegram через ваш прокси (вариант 3)",
            "То же, но крупная нарезка прокси-хендшейка. Ещё один профиль на случай, если основной и " +
            "вариант 2 на вашем операторе связи не пробивают. Сначала укажите хост прокси.",
            recommended: false,
            discordTls: RecDiscordTls, youtubeTls: RecYoutubeTls, fallbackTls: RecDiscordTls,
            proxyTls: new[] { "--lua-desync=multidisorder:pos=128,256,384" }),

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
            discordTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=6",
                                "--lua-desync=fakedsplit:tcp_ts=1000" },
            youtubeTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=6",
                                "--lua-desync=fakedsplit:tcp_ts=1000" },
            fallbackTls: new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=1000:repeats=6",
                                 "--lua-desync=fakedsplit:tcp_ts=1000" }),

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
                // discord.media control socket (TLS) — лёгкая разрезка, чтобы пускало в войс.
                "--filter-tcp=443-65535", "--filter-l7=tls", "{EXCLUDE:exclude}", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                "--new",
                // Голос: discord+stun → фейк QUIC-блобом, без ttl-ограничения.
                "--filter-udp=19294-19344,50000-50100", "--filter-l7=discord,stun",
                  "--lua-desync=fake:blob=quic_google:repeats=6",
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
        string discordFilter = "{HOSTLIST:discord}", string[]? proxyTls = null)
        => new()
        {
            Name = name,
            Description = description,
            IsBuiltIn = true,
            IsRecommended = recommended,
            RequiresProxyHost = proxyTls is not null,
            Args = BuildComboArgs(discordTls, youtubeTls, fallbackTls, discordFilter, proxyTls),
        };

    /// <summary>Build the shared combo argument list (per-service TLS bundles + QUIC + Discord voice).
    /// Reused by the strategy generator to assemble a personal preset from generated TLS bundles.</summary>
    public static List<string> BuildComboArgs(
        string[] discordTls, string[] youtubeTls, string[] fallbackTls,
        string discordFilter = "{HOSTLIST:discord}", string[]? proxyTls = null)
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
        // NB: Telegram is intentionally NOT handled by the base combo — TG lives only in the dedicated
        // "через ваш прокси" presets (the proxyTls profile below). The base combo stays DC+YT+voice.
        // 2d) ee-MTProxy (опционально): рукопожатие FakeTLS уходит TLS-ClientHello'ом к ПРОИЗВОЛЬНОМУ
        //     SNI прокси, поэтому ловим строго по IP прокси-сервера ({IPSET:proxy}), а не по SNI/домену.
        //     Заскоупено по IP → YouTube/Discord/прочий TLS НЕ затрагивает (в отличие от широкого
        //     catch-all, который ломал ютуб). Десинк — победивший в тестах multidisorder. Профиль
        //     активен только если задан хост прокси (иначе движок не стартует — RequiresProxyHost).
        if (proxyTls is not null)
        {
            a.Add("--new");
            a.AddRange(new[] { "--filter-tcp=443-65535", "{IPSET:proxy}", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello" });
            a.AddRange(proxyTls);
        }
        // 3) Остальной TLS (вкл. hCaptcha и пр.). Catch-all — поэтому исключаем чувствительные
        //    домены (банки/госуслуги/VK/Яндекс/Steam/…) через {EXCLUDE:exclude}, чтобы их не сломать.
        a.Add("--new");
        a.AddRange(new[] { "--filter-tcp=443-65535", "--filter-l7=tls", "{EXCLUDE:exclude}", "--out-range=-d10", "--payload=tls_client_hello" });
        a.AddRange(fallbackTls);
        // 4) QUIC YouTube (по SNI) → google-фейк.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{HOSTLIST:youtube}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=quic_google:repeats=11" });
        // 5) QUIC остальное → дефолтный фейк. Тоже catch-all → исключаем чувствительные домены.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=443-65535", "--filter-l7=quic", "{EXCLUDE:exclude}", "--payload=quic_initial",
                           "--lua-desync=fake:blob=fake_default_quic:repeats=6" });
        // 6) Голос Discord (STUN + IP-discovery): фейк QUIC-блобом google. Голосовой сервер
        //    отбрасывает QUIC как мусор для своего потока → SSRC не портится, нет NO_ROUTE,
        //    поэтому ttl НЕ режем (route-independent). Проверенный рабочий путь — Flowseal alt10:
        //    --filter-l7=discord,stun → fake quic_initial_www_google_com, repeats=6.
        a.Add("--new");
        a.AddRange(new[] { "--filter-udp=19294-19344,50000-50100", "--filter-l7=discord,stun",
                           "--lua-desync=fake:blob=quic_google:repeats=6" });

        return a;
    }

}
