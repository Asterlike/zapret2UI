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

    public static List<Preset> BuiltIns() => new()
    {
        new Preset
        {
            Name = "Общий (HTTP + TLS + QUIC)",
            Description = "Базовый обход для большинства сайтов: 80/443 TCP и 443 UDP (QUIC). " +
                          "Хорошая отправная точка.",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-tcp-out=80,443", "--wf-udp-out=443",
                "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req",
                  "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
                  "--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
                "--new",
                "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5",
                  "--lua-desync=multidisorder:pos=midsld",
                "--new",
                "--filter-udp=443", "--filter-l7=quic", "--payload=quic_initial",
                  "--lua-desync=fake:blob=fake_default_quic:repeats=2",
            }
        },
        new Preset
        {
            Name = "YouTube",
            Description = "Усиленная стратегия для YouTube/Google. Подменяет SNI на www.google.com. " +
                          "Подключите хостлист с доменами youtube/googlevideo.",
            IsBuiltIn = true,
            UsesHostlist = true,
            Args = new()
            {
                "--wf-tcp-out=443", "--wf-udp-out=443",
                "--filter-tcp=443", "--filter-l7=tls", "{HOSTLIST}", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
                  "--lua-desync=multidisorder:pos=1,midsld",
                "--new",
                "--filter-udp=443", "--filter-l7=quic", "{HOSTLIST}", "--payload=quic_initial",
                  "--lua-desync=fake:blob=fake_default_quic:repeats=11",
            }
        },
        new Preset
        {
            Name = "Discord",
            Description = "TLS-обход + фейки для голосового трафика Discord (STUN / IP discovery).",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-tcp-out=443", "--wf-udp-out=443,50000-65535",
                "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
                  "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6",
                  "--lua-desync=multidisorder:pos=midsld",
                "--new",
                "--filter-l7=stun,discord", "--payload=stun,discord_ip_discovery",
                  "--lua-desync=fake:blob=0x00000000000000000000000000000000:repeats=2",
            }
        },
        new Preset
        {
            Name = "Только QUIC",
            Description = "Минимальная стратегия: фейк для QUIC Initial на udp/443.",
            IsBuiltIn = true,
            Args = new()
            {
                "--wf-udp-out=443",
                "--filter-udp=443", "--filter-l7=quic", "--payload=quic_initial",
                  "--lua-desync=fake:blob=fake_default_quic:repeats=2",
            }
        },
    };
}
