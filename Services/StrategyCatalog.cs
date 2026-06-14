namespace ZapretUI.Services;

/// <summary>A single candidate strategy: the winws2 args that follow the profile filters.</summary>
public sealed record StrategyCandidate(string Name, string[] Desync);

/// <summary>
/// Candidate strategies tried by the auto-tester, distilled from zapret2 blockcheck2.d.
/// Each Desync array is the list of args AFTER the profile filters
/// (--filter-* / --out-range / --payload), ordered simple -> complex.
/// Only the engine-auto-initialized fake blobs (fake_default_tls / _http / _quic)
/// are used: no external file blobs, no {WF}/{FILES} tokens, no --blob= definitions.
/// </summary>
public static class StrategyCatalog
{
    // TLS / HTTPS (tcp 443, payload tls_client_hello)
    public static readonly StrategyCandidate[] Tls =
    {
        // --- plain split / disorder (no fake) ---
        new("multisplit midsld", new[] { "--lua-desync=multisplit:pos=midsld" }),
        new("multisplit sniext+1", new[] { "--lua-desync=multisplit:pos=sniext+1" }),
        new("multisplit 1,midsld", new[] { "--lua-desync=multisplit:pos=1,midsld" }),
        new("multidisorder midsld", new[] { "--lua-desync=multidisorder:pos=midsld" }),
        new("multidisorder 1,midsld", new[] { "--lua-desync=multidisorder:pos=1,midsld" }),

        // --- plain fake (md5sig fooling) ---
        new("fake md5sig", new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5" }),
        new("fake md5sig x6", new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6" }),
        new("fake seq-10000 md5sig x6", new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:tcp_seq=-10000:repeats=6" }),
        new("fake autottl md5sig", new[] { "--lua-desync=fake:blob=fake_default_tls:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5" }),

        // --- fakedsplit / fakeddisorder ---
        new("fakedsplit midsld md5sig", new[] { "--lua-desync=fakedsplit:pos=midsld:tcp_md5" }),
        new("fakedsplit 1,midsld md5sig", new[] { "--lua-desync=fakedsplit:pos=1,midsld:tcp_md5" }),
        new("fakeddisorder midsld md5sig", new[] { "--lua-desync=fakeddisorder:pos=midsld:tcp_md5" }),

        // --- fake + multisplit / multidisorder combos ---
        new("fake md5sig + multisplit midsld", new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6", "--lua-desync=multisplit:pos=midsld" }),
        new("fake md5sig + multidisorder midsld", new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6", "--lua-desync=multidisorder:pos=midsld" }),
        new("fake md5sig + multidisorder 1,midsld", new[] { "--lua-desync=fake:blob=fake_default_tls:tcp_md5:repeats=6", "--lua-desync=multidisorder:pos=1,midsld" }),

        // --- seqovl (tcp window overlap, no fooling needed) ---
        new("multisplit sniext+1 seqovl", new[] { "--lua-desync=multisplit:pos=sniext+1:seqovl=1" }),
        new("tcpseg seqovl + drop", new[] { "--lua-desync=tcpseg:pos=0,-1:seqovl=1", "--lua-desync=drop" }),

        // --- syndata (data in SYN) ---
        new("syndata tls fake", new[] { "--lua-desync=syndata:blob=fake_default_tls:tls_mod=rnd,dupsid,rndsni" }),

        // --- oob (out-of-band byte) ---
        new("oob midsld", new[] { "--lua-desync=oob:urp=midsld" }),
    };

    // HTTP (tcp 80, payload http_req)
    public static readonly StrategyCandidate[] Http =
    {
        // --- header-case / eol tricks ---
        new("http hostcase", new[] { "--lua-desync=http_hostcase" }),
        new("http methodeol", new[] { "--lua-desync=http_methodeol" }),

        // --- plain split / disorder ---
        new("multisplit method+2", new[] { "--lua-desync=multisplit:pos=method+2" }),
        new("multidisorder method+2,midsld", new[] { "--lua-desync=multidisorder:pos=method+2,midsld" }),

        // --- plain fake ---
        new("fake md5sig", new[] { "--lua-desync=fake:blob=fake_default_http:tcp_md5" }),
        new("fake autottl md5sig", new[] { "--lua-desync=fake:blob=fake_default_http:ip_autottl=-2,3-20:ip6_autottl=-2,3-20:tcp_md5" }),

        // --- fakedsplit ---
        new("fakedsplit method+2 md5sig", new[] { "--lua-desync=fakedsplit:pos=method+2:tcp_md5" }),

        // --- fake + multisplit combo ---
        new("fake md5sig + multisplit method+2", new[] { "--lua-desync=fake:blob=fake_default_http:tcp_md5", "--lua-desync=multisplit:pos=method+2" }),

        // --- tcpseg / seqovl ---
        new("tcpseg 0,midsld ip_id rnd", new[] { "--lua-desync=tcpseg:pos=0,midsld:ip_id=rnd:repeats=2" }),
        new("tcpseg seqovl + drop", new[] { "--lua-desync=tcpseg:pos=0,-1:seqovl=1", "--lua-desync=drop" }),

        // --- syndata ---
        new("syndata http fake", new[] { "--lua-desync=syndata:blob=fake_default_http" }),

        // --- oob ---
        new("oob midsld", new[] { "--lua-desync=oob:urp=midsld" }),
    };

    // QUIC (udp 443, payload quic_initial)
    public static readonly StrategyCandidate[] Quic =
    {
        new("fake x2", new[] { "--lua-desync=fake:blob=fake_default_quic:repeats=2" }),
        new("fake x6", new[] { "--lua-desync=fake:blob=fake_default_quic:repeats=6" }),
        new("fake x11", new[] { "--lua-desync=fake:blob=fake_default_quic:repeats=11" }),
        new("fake x6 + ipfrag + drop", new[] { "--lua-desync=fake:blob=fake_default_quic:repeats=6", "--lua-desync=send:ipfrag:ipfrag_pos_udp=32", "--lua-desync=drop" }),
        new("ipfrag + drop", new[] { "--lua-desync=send:ipfrag:ipfrag_pos_udp=32", "--lua-desync=drop" }),
    };
}
