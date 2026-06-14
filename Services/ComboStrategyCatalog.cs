namespace ZapretUI.Services;

/// <summary>A full, ready-to-run winws2 strategy (multi-profile) the auto-selector tries.</summary>
public sealed record ComboStrategy(string Name, List<string> Args);

/// <summary>
/// The candidate "configs" the auto-selector cycles through — each a complete
/// multi-profile winws2 strategy that handles HTTP + TLS + QUIC (general) AND
/// Discord voice (STUN + IP-discovery) in one go, differing only in the TLS/QUIC
/// desync flavour. The selector launches each, scores it against the goal
/// endpoints, and keeps the one with the fewest failures — so a single chosen
/// strategy works for YouTube and Discord together.
/// </summary>
public static class ComboStrategyCatalog
{
    private static List<string> Build(string[] tls, string[] quic) => new List<string>
    {
        "--wf-tcp-out=80,443",
        "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
        "--blob=disc_stun:@{FILES}\\fake\\stun.bin",
        "--blob=disc_ipd:@{FILES}\\fake\\discord-ip-discovery-with-port.bin",
        "--wf-raw-part=@{WF}\\windivert_part.discord_media.txt",
        "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
        "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
        // HTTP (shared across variants)
        "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req",
          "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
          "--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
        // TLS (variant)
        "--new",
        "--filter-tcp=443", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
    }
    .Concat(tls)
    .Concat(new[]
    {
        // QUIC (variant)
        "--new",
        "--filter-udp=443", "--filter-l7=quic", "--payload=quic_initial",
    })
    .Concat(quic)
    .Concat(new[]
    {
        // Discord voice: STUN handshake (fake + autottl, like the proven recipe)
        "--new",
        "--filter-l7=stun", "--payload=stun",
          "--lua-desync=fake:blob=disc_stun:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
        // Discord voice: IP discovery
        "--new",
        "--filter-l7=discord", "--payload=discord_ip_discovery",
          "--lua-desync=fake:blob=disc_ipd:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=6",
    })
    .ToList();

    // Default QUIC handling reused by most variants.
    private static readonly string[] QuicFake6 = { "--lua-desync=fake:blob=fake_default_quic:repeats=6" };
    private static readonly string[] QuicFake11 = { "--lua-desync=fake:blob=fake_default_quic:repeats=11" };

    public static IReadOnlyList<ComboStrategy> All { get; } = new List<ComboStrategy>
    {
        // --- window-size (wssize): zapret2's strongest lever for stubborn blocks
        //     like Discord login — forces the server to split its response so DPI
        //     cannot reassemble it. Tried first.
        new("Окно wssize 1:6", Build(
            new[] { "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake6)),

        new("Окно wssize 1:6 + multisplit", Build(
            new[] { "--lua-desync=multisplit:pos=midsld",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake6)),

        new("Окно wssize 1:6 + FAKE md5sig", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake11)),

        // --- fake / split family
        new("FAKE md5sig + multidisorder", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6",
                    "--lua-desync=multidisorder:pos=midsld" }, QuicFake6)),

        new("FAKE md5sig + multidisorder 1,midsld", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6",
                    "--lua-desync=multidisorder:pos=1,midsld" }, QuicFake11)),

        new("multisplit seqovl", Build(
            new[] { "--lua-desync=multisplit:pos=sniext+1:seqovl=1" }, QuicFake6)),

        new("fakedsplit md5sig", Build(
            new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5" }, QuicFake11)),

        new("FAKE autottl + multisplit", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5:repeats=6",
                    "--lua-desync=multisplit:pos=midsld" }, QuicFake6)),

        new("Окно wssize 1:6 + fakedsplit", Build(
            new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake6)),

        // --- proven Discord stack from youtubediscord/zapret, translated to zapret2:
        //     fooling badseq -> tcp_seq offset, md5sig -> tcp_md5, autottl -> ip_autottl,
        //     fake-tls-mod=rnd,dupsid, split sld+1, dup -> repeats.
        new("DC: fake badseq + multisplit sld+1", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                    "--lua-desync=multisplit:pos=sld+1" }, QuicFake11)),

        new("DC: fake md5sig + multisplit sld+1 dupsid", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                    "--lua-desync=multisplit:pos=sld+1" }, QuicFake6)),

        new("DC: fake badsum + multidisorder sld+1", Build(
            new[] { "--lua-desync=fake:blob=fake_default_tls:badsum:ip_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                    "--lua-desync=multidisorder:pos=sld+1,midsld" }, QuicFake11)),
    };
}
