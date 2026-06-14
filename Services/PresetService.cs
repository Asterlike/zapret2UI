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

    // ---- built-in strategies (documented winws2 syntax) --------------------

    // Strategies follow the official preset2_example from the zapret2 manual
    // (autottl / md5sig / seqovl / multidisorder, kernel-mode windivert raw-part
    // capture filters). DPI bypass is provider-specific — these are proven
    // starting points, not a guarantee for every ISP.
    public static List<Preset> BuiltIns() => new()
    {
        new Preset
        {
            Name = "Общий (рекомендуемый)",
            Description = "Эталонная базовая стратегия bol-van для HTTP+TLS+QUIC. Подходит большинству " +
                          "сайтов и не требует хостлиста. Начните с неё.",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-tcp-out=80,443",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
                "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req",
                  "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
                  "--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
                "--new",
                "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6",
                  "--lua-desync=multidisorder:pos=midsld",
                "--new",
                "--filter-udp=443", "--filter-l7=quic", "--payload=quic_initial",
                  "--lua-desync=fake:blob=fake_default_quic:repeats=6",
            }
        },
        new Preset
        {
            Name = "YouTube / Google",
            Description = "Усиленная стратегия для YouTube/Google: подмена SNI фейка на www.google.com, " +
                          "google-QUIC-фейк, 11 повторов. Выберите хостлист «youtube» для точечного применения.",
            IsBuiltIn = true,
            UsesHostlist = true,
            Args = new()
            {
                "--wf-tcp-out=443",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
                "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
                "--filter-tcp=443", "--filter-l7=tls", "{HOSTLIST}", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
                  "--lua-desync=multidisorder:pos=1,midsld",
                "--new",
                "--filter-udp=443", "--filter-l7=quic", "{HOSTLIST}", "--payload=quic_initial",
                  "--lua-desync=fake:blob=quic_google:repeats=11",
            }
        },
        new Preset
        {
            Name = "Discord (чат + голос)",
            Description = "TLS-обход + фейки для голосового трафика Discord: STUN, IP discovery, WireGuard. " +
                          "Захват медиапотоков фильтрами windivert в режиме ядра.",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-tcp-out=443",
                "--wf-raw-part=@{WF}\\windivert_part.discord_media.txt",
                "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
                "--wf-raw-part=@{WF}\\windivert_part.wireguard.txt",
                "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6",
                  "--lua-desync=multidisorder:pos=midsld",
                "--new",
                "--filter-l7=wireguard,stun,discord",
                  "--payload=wireguard_initiation,wireguard_cookie,stun,discord_ip_discovery",
                  "--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2",
            }
        },
        new Preset
        {
            Name = "Только QUIC",
            Description = "Минимальная стратегия: фейк для QUIC Initial. Захват QUIC-инициалов фильтром " +
                          "windivert в режиме ядра (экономит CPU).",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--filter-l7=quic", "--payload=quic_initial",
                  "--lua-desync=fake:blob=fake_default_quic:repeats=6",
            }
        },
        new Preset
        {
            Name = "Эталон bol-van (всё сразу)",
            Description = "Полная официальная стратегия preset2_example: HTTP, TLS (общий + YouTube), " +
                          "QUIC (общий + YouTube), Discord/STUN/WireGuard. Для YouTube-профилей выберите " +
                          "хостлист «youtube».",
            IsBuiltIn = true,
            UsesHostlist = true,
            Args = new()
            {
                "--wf-tcp-out=80,443",
                "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
                "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
                "--wf-raw-part=@{WF}\\windivert_part.discord_media.txt",
                "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
                "--wf-raw-part=@{WF}\\windivert_part.wireguard.txt",
                "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
                "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req",
                  "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
                  "--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
                "--new",
                "--filter-tcp=443", "--filter-l7=tls", "{HOSTLIST}", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
                  "--lua-desync=multidisorder:pos=1,midsld",
                "--new",
                "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6",
                  "--lua-desync=multidisorder:pos=midsld",
                "--new",
                "--filter-udp=443", "--filter-l7=quic", "{HOSTLIST}", "--payload=quic_initial",
                  "--lua-desync=fake:blob=quic_google:repeats=11",
                "--new",
                "--filter-udp=443", "--filter-l7=quic", "--payload=quic_initial",
                  "--lua-desync=fake:blob=fake_default_quic:repeats=11",
                "--new",
                "--filter-l7=wireguard,stun,discord",
                  "--payload=wireguard_initiation,wireguard_cookie,stun,discord_ip_discovery",
                  "--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2",
            }
        },
    };
}
