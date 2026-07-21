using Zapret2UI.Models;

namespace Zapret2UI.Services;

/// <summary>A full, ready-to-run winws2 strategy (multi-profile) the auto-selector tries.</summary>
public sealed record ComboStrategy(string Name, List<string> Args)
{
    /// <summary>How the auto-selector must probe this candidate. The ~30 global catalog entries below
    /// are catch-alls → probed with bypassAll=true so the goal hosts are actually desynced during the
    /// test (the default). An already SNI-scoped preset candidate is probed with bypassAll=false —
    /// exactly as it really runs, its own discord/youtube hostlists covering the goal hosts.</summary>
    public bool BypassAll { get; init; } = true;

    /// <summary>Non-null when this candidate IS a ready-made preset (the strategy saved for this
    /// network, or a built-in): the winner is then that preset itself, not a global TLS bundle to
    /// re-route into a scoped combo.</summary>
    public Preset? SourcePreset { get; init; }
}

/// <summary>
/// The candidate "configs" the auto-selector cycles through — each a complete
/// multi-profile winws2 strategy that handles HTTP + TLS + QUIC (general) AND
/// Discord voice (STUN) in one go, differing only in the TLS/QUIC
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
        "--blob=quic_google:@{FILES}\\fake\\quic_initial_www_google_com.bin",
        "--blob=tls_google:@{FILES}\\fake\\tls_clienthello_www_google_com.bin",
        // Отечественные фейк-блобы (ClientHello vk.com / sberbank.ru) — ТСПУ вайтлистит домашний
        // трафик, поэтому фейк «под vk/сбер» выживает там, где google-фейк режется (тренд 2026).
        "--blob=tls_vk:@{FILES}\\fake\\tls_clienthello_vk_com.bin",
        "--blob=tls_sber:@{FILES}\\fake\\tls_clienthello_sberbank_ru.bin",
        "--blob=tls_gos:@{FILES}\\fake\\tls_clienthello_gosuslugi_ru.bin",
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
        // Discord voice (STUN + IP-discovery) on the FULL high UDP range 50000-65535
        // (narrow 50000-50100 missed half the voice servers → 5000 ping). QUIC-google
        // blob (junk for the voice flow → SSRC never poisoned) + ip_autottl so the fake
        // dies on the provider DPI before the server (anti-drop), letting real RTP flow
        // without throttle. repeats=2 — the current 5k-ping fix (Flowseal #12614).
        "--new",
        "--filter-udp=19294-19344,50000-65535", "--filter-l7=discord,stun",
          "--lua-desync=fake:blob=quic_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:repeats=2",
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
                    "--lua-desync=multisplit:pos=2,midsld-2:seqovl=700:seqovl_pattern=tls_google:optional" }, QuicFake11)),

        // --- The legacy Discord recipe (Example 1): fake with tcp_ts then a
        //     multidisorder_legacy carrying a real ClientHello as the overlap.
        //     NOTE: in multidisorder(_legacy) `seqovl` is a POSITION MARKER
        //     (resolve_pos) that must be LESS than the first split pos — not a
        //     byte count like in multisplit. The old `seqovl=652` silently
        //     cancelled itself (652 >= split pos), so the overlap did nothing.
        new("DC legacy: fake ts → multidisorder sld seqovl", Build(
            new[] { "--lua-desync=fake:blob=tls_google:repeats=6:tcp_ts=-1000",
                    "--lua-desync=multidisorder_legacy:pos=1,sld+2,midsld:seqovl=sld+1:seqovl_pattern=tls_google:optional" }, QuicFake6)),

        // --- The discord.media recipe (high TCP ports): syndata in the SYN, the
        //     heavy fooling (bad-ack + low autottl) carried by a FAKE, then a
        //     CLEAN multisplit. The old version put tcp_ack=-66000 / ip_ttl=4 /
        //     repeats=10 directly on the multisplit — i.e. on the REAL segments,
        //     which corrupts the ACK and drops the real ClientHello before the
        //     server (ttl 4). Fooling belongs on fakes, never on real splits.
        new("DC media: syndata → fake badack → multisplit seqovl", Build(
            new[] { "--lua-desync=send:repeats=2",
                    "--lua-desync=syndata:blob=tls_google:ip_autottl=-2,3-20",
                    "--lua-desync=fake:blob=tls_google:tcp_ack=-66000:tcp_ts_up:ip_autottl=-2,3-20:repeats=6",
                    "--lua-desync=multisplit:pos=1,midsld:seqovl=336:seqovl_pattern=tls_google:optional" }, QuicFake11)),

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
            new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_ts=-1000:repeats=6",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake11)),

        // --- Flowseal ALT11/ALT12 ("fooling ts"): a fake (ts, repeats=8) PREPENDED to a multisplit
        //     carrying a big byte-seqovl with a real google ClientHello pattern. The fake-then-seqovl
        //     stack is distinct from the bare-seqovl candidates above; translated from general (ALT11/12).
        new("ALT11/12: fake ts → multisplit seqovl 681", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=8",
                    "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }, QuicFake11)),

        new("ALT: fake ts → multisplit seqovl 664", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=8",
                    "--lua-desync=multisplit:pos=1,midsld:seqovl=664:seqovl_pattern=tls_google:optional" }, QuicFake11)),

        // --- YouTube TCP (real preset): split ClientHello around the SNI with a
        //     real-CH seqovl pattern. The single best general-purpose TLS split.
        new("YT: multisplit 2,midsld-2 seqovl tls", Build(
            new[] { "--lua-desync=multisplit:pos=2,midsld-2:seqovl=1:seqovl_pattern=tls_google:optional" }, QuicFake6)),

        // --- googlevideo (real preset): multi-marker multidisorder, reverse-order
        //     segments across host/sld/sni — defeats reassembly-by-seq DPI.
        new("YT googlevideo: multidisorder multi-pos", Build(
            new[] { "--lua-desync=multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2" }, QuicFake6)),

        // --- YouTube current community recipe (июль 2026): a dupsid-fooled fake ahead of a rich
        //     SNI-marker multidisorder. Verbs/markers verified against the local engine lua.
        new("YT актуальный: fake dupsid → multidisorder SNI", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tls_mod=rnd,dupsid:repeats=6",
                    "--lua-desync=multidisorder:pos=1,sniext+1,host+1,midsld-2,midsld,endhost-1" }, QuicFake11)),

        // --- fake + fooling families (broad coverage)
        new("FAKE md5sig + multidisorder midsld", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_md5:tcp_seq=-10000:repeats=6",
                    "--lua-desync=multidisorder:pos=1,midsld" }, QuicFake11)),

        new("FAKE autottl + multisplit midsld", Build(
            new[] { "--lua-desync=fake:blob=tls_google:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5:repeats=6",
                    "--lua-desync=multisplit:pos=midsld" }, QuicFake6)),

        new("fakedsplit md5sig midsld", Build(
            new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5" }, QuicFake11)),

        // --- hostfakesplit: splits the ClientHello around the SNI and injects a FAKE host segment
        //     (www.google.com) so the DPI sees a google connection and can't reassemble the real
        //     Discord SNI. On reassembling DPIs (where plain split/seqovl fail) this is often the
        //     ONLY family that passes login — so several variants are in the pool.
        new("hostfakesplit ts md5", Build(
            new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4" }, QuicFake6)),

        new("hostfakesplit md5 ×6", Build(
            new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_md5:repeats=6" }, QuicFake6)),

        new("hostfakesplit + wssize", Build(
            new[] { "--lua-desync=hostfakesplit:host=www.google.com:tcp_ts=-1000:tcp_md5:repeats=4",
                    "--lua-desync=wssize:wsize=1:scale=6" }, QuicFake6)),

        new("hostfakesplit MS-host", Build(
            new[] { "--lua-desync=hostfakesplit:host=www.microsoft.com:tcp_ts=-1000:tcp_md5:repeats=4" }, QuicFake6)),

        // --- Отечественный фейк (тренд 2026: sonicdpi ozon.ru, Flowseal dbankcloud_ru). ТСПУ вайтлистит
        //     домашний трафик, поэтому фейковый SNI/ClientHello «под vk/ozon» переживает там, где
        //     google-фейк режется. host= — просто строка-домен (blob не нужен); tls_vk — реальный
        //     ClientHello vk.com из поставки движка.
        new("hostfakesplit VK-host ts md5", Build(
            new[] { "--lua-desync=hostfakesplit:host=vk.com:tcp_ts=-1000:tcp_md5:repeats=4" }, QuicFake6)),

        new("hostfakesplit ozon.ru md5 ×6", Build(
            new[] { "--lua-desync=hostfakesplit:host=ozon.ru:tcp_md5:repeats=6" }, QuicFake6)),

        new("DC отеч.: fake VK-CH md5 → multidisorder", Build(
            new[] { "--lua-desync=fake:blob=tls_vk:tcp_md5:ip_autottl=-2,3-20:repeats=6",
                    "--lua-desync=multidisorder:pos=1,midsld" }, QuicFake11)),

        new("DC отеч.: fake Sber-CH md5 → multidisorder", Build(
            new[] { "--lua-desync=fake:blob=tls_sber:tcp_md5:ip_autottl=-2,3-20:repeats=6",
                    "--lua-desync=multidisorder:pos=1,midsld" }, QuicFake11)),

        // Ещё отечественные SNI (gateway-safe hostfakesplit) + gosuslugi-фейк — шире перебор под РФ.
        new("hostfakesplit Sber-host ts md5", Build(
            new[] { "--lua-desync=hostfakesplit:host=sberbank.ru:tcp_ts=-1000:tcp_md5:repeats=4" }, QuicFake6)),

        new("hostfakesplit Gos-host ts md5", Build(
            new[] { "--lua-desync=hostfakesplit:host=gosuslugi.ru:tcp_ts=-1000:tcp_md5:repeats=4" }, QuicFake6)),

        new("DC отеч.: fake Gos-CH md5 → multidisorder", Build(
            new[] { "--lua-desync=fake:blob=tls_gos:tcp_md5:ip_autottl=-2,3-20:repeats=6",
                    "--lua-desync=multidisorder:pos=1,midsld" }, QuicFake11)),

        // Flowseal ALT10/ALT11 (переводы на nfqws2) — gateway-friendly fake:ts прайм. ALT10 = чистый
        // двойной fake (google + vk); ALT11 = fake:ts + multisplit seqovl. Часто пробивают гейтвей/медиа.
        new("Flowseal ALT10: двойной fake ts", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                    "--lua-desync=fake:blob=tls_vk:tcp_ts=-1000:repeats=6" }, QuicFake6)),

        new("Flowseal ALT11: fake ts → seqovl", Build(
            new[] { "--lua-desync=fake:blob=tls_google:tcp_ts=-1000:repeats=6",
                    "--lua-desync=multisplit:pos=1,midsld:seqovl=681:seqovl_pattern=tls_google:optional" }, QuicFake6)),

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
