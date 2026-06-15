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
        // Wide capture — Discord media is on high TCP ports (2053/8443…) and voice
        // on high UDP ports (50000+); capturing only 443 misses half of Discord.
        "--wf-tcp-out=80,443-65535",
        "--wf-udp-out=443-65535",
        // Connection-tracking / ip-cache tuning (from the reference zapret2 preset).
        "--ctrack-disable=0",
        "--ipcache-lifetime=8400",
        "--ipcache-hostname=1",
        "--lua-init=fake_default_tls = tls_mod(fake_default_tls,'rnd,rndsni')",
        "--blob=disc_stun:@{FILES}\\fake\\stun.bin",
        "--blob=disc_ipd:@{FILES}\\fake\\discord-ip-discovery-with-port.bin",
        "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
        "--wf-raw-part=@{WF}\\windivert_part.discord_media.txt",
        "--wf-raw-part=@{WF}\\windivert_part.stun.txt",
        "--wf-raw-part=@{WF}\\windivert_part.quic_initial_ietf.txt",
        // HTTP (shared across variants)
        "--filter-tcp=80", "--filter-l7=http", "--out-range=-d10", "--payload=http_req",
          "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
          "--lua-desync=fakedsplit:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5",
        // TLS on any high port (covers HTTPS + Discord media) (variant)
        "--new",
        "--filter-tcp=443-65535", "--filter-l7=tls", "--out-range=-d10", "--payload=tls_client_hello",
    }
    .Concat(tls)
    .Concat(new[]
    {
        // QUIC on any high UDP port (variant)
        "--new",
        "--filter-udp=443-65535", "--filter-l7=quic", "--payload=quic_initial",
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
        // ===================================================================
        //  Candidates rebuilt from the REAL working zapret2 presets (verified
        //  against the locally installed engine lua). The key lesson: working
        //  configs chain 3-4 desyncs per profile — a prelude `send`, a `syndata`
        //  in the SYN, a `fake` with strong fooling (tcp_ack=-66000:tcp_ts_up),
        //  then a `multisplit`/`multidisorder` with a real ClientHello as the
        //  seqovl_pattern. Single-stage strategies (our old pool) rarely beat
        //  Discord login. `tls_google` = tls_clienthello_www_google_com.bin
        //  (the only large real-CH blob we ship; the presets' tls5/tls7 are
        //  youtubediscord-custom and absent here).
        // ===================================================================

        // --- The proven Discord pipeline (youtubediscord Example 2), translated
        //     1:1 to stock verbs. This is the strongest candidate for login.
        new("DC: send→syndata→fake ack→multisplit seqovl", Build(
            new[] { "--lua-desync=send:repeats=2",
                    "--lua-desync=syndata:blob=tls_google",
                    "--lua-desync=fake:blob=tls_google:tcp_ack=-66000:tcp_ts_up:tls_mod=rnd:repeats=2",
                    "--lua-desync=multisplit:pos=2,midsld-2:seqovl=700:seqovl_pattern=tls_google:tcp_flags_unset=ack:optional" }, QuicFake11)),

        // --- The legacy Discord recipe (Example 1): fake with tcp_ts then a
        //     big seqovl multidisorder_legacy carrying a real ClientHello.
        new("DC legacy: fake ts → multidisorder_legacy 652", Build(
            new[] { "--lua-desync=fake:blob=tls_google:repeats=6:tcp_ts=1000",
                    "--lua-desync=multidisorder_legacy:seqovl=652:seqovl_pattern=tls_google:optional" }, QuicFake6)),

        // --- The discord.media recipe (high TCP ports): syndata + a heavily
        //     fooled multisplit (low ttl, wrong ack). Good for media/gateway.
        new("DC media: syndata autottl → multisplit ttl4", Build(
            new[] { "--lua-desync=send:repeats=2",
                    "--lua-desync=syndata:blob=tls_google:ip_autottl=-2,3-20",
                    "--lua-desync=multisplit:pos=1,midsld:repeats=10:tcp_ack=-66000:tcp_ts_up:ip_ttl=4:ip6_ttl=4:optional" }, QuicFake11)),

        // --- window-size (wssize): zapret2's strongest ZERO-PHASE lever for
        //     stubborn blocks like Discord login — forces the server to split
        //     its response so DPI cannot reassemble it. Needs --ipcache-hostname
        //     (set in the globals) to work with hostlists.
        new("Окно wssize 1:6", Build(
            new[] { "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake6)),

        new("Окно wssize 1:6 + seqovl tls_google", Build(
            new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake6)),

        new("Окно wssize 1:6 + fake ts md5", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_ts=1000:repeats=6",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake11)),

        // --- YouTube TCP (real preset): split ClientHello around the SNI with a
        //     real-CH seqovl pattern. The single best general-purpose TLS split.
        new("YT: multisplit 2,midsld-2 seqovl tls", Build(
            new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional" }, QuicFake6)),

        // --- googlevideo (real preset): multi-marker multidisorder, reverse-order
        //     segments across host/sld/sni — defeats reassembly-by-seq DPI.
        new("YT googlevideo: multidisorder multi-pos", Build(
            new[] { "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1" }, QuicFake6)),

        // --- fake + fooling families (broad coverage)
        new("FAKE md5sig + multidisorder midsld", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
                    "--lua-desync=multidisorder:pos=1,midsld" }, QuicFake11)),

        new("FAKE autottl + multisplit midsld", Build(
            new[] { "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5:repeats=6",
                    "--lua-desync=multisplit:pos=midsld" }, QuicFake6)),

        new("fakedsplit md5sig midsld", Build(
            new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5" }, QuicFake11)),

        // --- hostfakesplit (real Example 2 ozon recipe): generates a fake host,
        //     splits at it, interleaves fakes. Useful when SNI is the trigger.
        new("hostfakesplit ts md5", Build(
            new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" }, QuicFake6)),

        // --- proven Discord stack (badseq/dupsid + split at sld+1)
        new("DC: fake badseq dupsid → multisplit sld+1", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_seq=-10000:ip_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                    "--lua-desync=multisplit:pos=sld+1" }, QuicFake11)),

        new("DC: fake badsum dupsid → multidisorder sld+1", Build(
            new[] { "--lua-desync=fake:blob=tls_google:badsum:ip_autottl=-2,3-20:tls_mod=rnd,dupsid:repeats=2",
                    "--lua-desync=multidisorder:pos=sld+1,midsld" }, QuicFake6)),

        // --- seqovl-only with a real ClientHello pattern (no fooling needed —
        //     sequence manipulation, not header corruption).
        new("SEQOVL tls_google 652 pos=2", Build(
            new[] { "--lua-desync=multisplit:pos=2:seqovl=652:seqovl_pattern=tls_google:optional" }, QuicFake6)),
    };
}
