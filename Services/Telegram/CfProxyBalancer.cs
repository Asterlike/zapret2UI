using System.Collections.Concurrent;
using System.Text;

namespace Zapret2UI.Services.Telegram;

/// <summary>Permission to use the direct DC IP, handed out by <see cref="CfProxyBalancer.TryBeginDirect"/>:
/// <c>Known</c> — a recent probe succeeded, just use it; <c>Probe</c> — the path's state is unknown and
/// this connection is the one testing it; <c>Skip</c> — cooling down, or another connection is already
/// probing, so go to the fronts instead of piling onto the same timeout.</summary>
internal enum DirectTicket { Skip, Known, Probe }

/// <summary>Cloudflare-fronted domain pool used to reach Telegram DCs when the direct IP is blocked.
/// Ported from tg-ws-proxy's obfuscated default list + balancer (a per-DC "sticky" pick, then the
/// rest shuffled).</summary>
internal sealed class CfProxyBalancer
{
    private const string Suffix = ".co.uk";

    private static readonly string[] Encoded =
    {
        "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com",
        "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
        "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com",
        "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com",
    };

    /// <summary>The decoded Cloudflare fronting base domains (…co.uk). Exposed so the engine can seed a
    /// hostlist and desync the proxy's OWN upstream TLS — mobile DPI (TSPU) corrupts the tunnel
    /// mid-stream, and only a continuous packet-level desync on these connections survives it. Declared
    /// after <see cref="Encoded"/> so the static initializer sees the source array (textual order).</summary>
    public static IReadOnlyList<string> AllBaseDomains { get; } = Encoded.Select(Decode).ToArray();

    /// <summary>How long a successful direct-path connect is trusted, so the connections that follow
    /// use it straight away instead of queueing behind a fresh probe.</summary>
    private const int DirectGoodMs = 60_000;

    private readonly string[] _domains;
    private readonly ConcurrentDictionary<string, string> _sticky = new(); // "dc|lane" → preferred front
    private readonly ConcurrentDictionary<string, long> _bad = new();      // "dc|lane|domain" → expiry
    private readonly ConcurrentDictionary<int, long> _directGoodUntil = new(); // dc → direct path trusted until
    private readonly ConcurrentDictionary<int, byte> _directProbe = new();     // dc → a probe is in flight
    private int _mediaTurn;                                                // round-robin cursor, media lane

    public CfProxyBalancer() => _domains = Encoded.Select(Decode).ToArray();

    /// <summary>Pseudo-front under which the direct DC IP shares this cooldown store. It has no base
    /// domain of its own, but it fails the same way (and costs the same 8 s timeout) when blocked.</summary>
    public const string DirectKey = "direct";

    /// <summary>Traffic lane. Telegram opens its media connections (file upload/download) separately
    /// from the chat/API one — and a client fetching a file opens SEVERAL at once. Every front resolves
    /// to one Cloudflare worker, so without separate lanes the whole download plus the chat funnel
    /// through a single worker: media crawls (or 429s) while chat looks perfect — the classic "всё
    /// работает, но медиа не грузится" — and a cooldown earned by a download benches the chat's front
    /// too. Verified by probe: the fronts have no media edge of their own (kws{dc}-1.{front} does not
    /// resolve), so media and chat genuinely share one hostname and must be spread out here instead.</summary>
    private static string Lane(bool isMedia) => isMedia ? "m" : "a";

    /// <summary>Fronts to try for this DC, best first. The chat lane keeps a per-DC sticky front so a
    /// long-lived connection stays put; the media lane instead starts one front further along on every
    /// call, so N parallel transfers spread over N workers. Fronts recently seen not to relay (see
    /// <see cref="MarkBad"/>) are skipped — per lane, so the two never bench each other.</summary>
    public IEnumerable<string> DomainsForDc(int dc, bool isMedia = false)
    {
        string lane = Lane(isMedia);
        var fresh = _domains.Where(d => !IsBad(dc, lane, d)).ToList();

        // A blackout cools every front down at once. Yielding nothing there would turn a temporary
        // outage into "нет доступных адресов" with no attempt made at all — and nothing would ever
        // clear the cooldowns, since only a real attempt can. Cooldowns are a preference, not a ban.
        if (fresh.Count == 0)
        {
            foreach (string d in _domains.OrderBy(_ => Random.Shared.Next())) yield return d;
            yield break;
        }

        if (isMedia)
        {
            int start = (int)((uint)Interlocked.Increment(ref _mediaTurn) % (uint)fresh.Count);
            for (int i = 0; i < fresh.Count; i++) yield return fresh[(start + i) % fresh.Count];
            yield break;
        }

        string sticky = _sticky.GetOrAdd($"{dc}|{lane}", _ => _domains[Random.Shared.Next(_domains.Length)]);
        if (fresh.Contains(sticky)) yield return sticky;
        foreach (string d in fresh.Where(d => d != sticky).OrderBy(_ => Random.Shared.Next())) yield return d;
    }

    /// <summary>Is this front (or <see cref="DirectKey"/>) still cooling down for this DC? The direct IP
    /// is lane-independent — a blocked Telegram edge is blocked for media and chat alike.</summary>
    public bool IsCooling(int dc, string key, bool isMedia = false) => IsBad(dc, Lane(isMedia), key);

    /// <summary>Cool a front down for this DC and lane (skip it in <see cref="DomainsForDc"/> for
    /// <paramref name="cooldownMs"/>, then it heals). Default ~2 min for a non-relaying front; callers
    /// pass shorter windows for softer reasons (e.g. a CF 429 or an instantly-dropped connection).</summary>
    public void MarkBad(int dc, string baseDomain, int cooldownMs = 120_000, bool isMedia = false) =>
        _bad[$"{dc}|{Lane(isMedia)}|{baseDomain}"] = Environment.TickCount64 + cooldownMs;

    private bool IsBad(int dc, string lane, string baseDomain) =>
        _bad.TryGetValue($"{dc}|{lane}|{baseDomain}", out long exp) && exp > Environment.TickCount64;

    /// <summary>May this connection try the direct DC IP, and does it own the probe?
    ///
    /// Telegram opens its connections in bursts. Where the direct edge is blocked every one of them
    /// used to see an un-cooled path, and they ALL burned the full connect timeout (twice — one per
    /// hostname) before the first of them got to mark it bad; the whole burst stalled together, again
    /// every time the cooldown lapsed. Now exactly one connection probes an unknown path while the
    /// rest go straight to the fronts, and a probe that succeeds opens the path for everyone that
    /// follows (<see cref="DirectGoodMs"/>) instead of leaving them to re-probe one by one.</summary>
    public DirectTicket TryBeginDirect(int dc)
    {
        if (IsCooling(dc, DirectKey)) return DirectTicket.Skip;
        if (_directGoodUntil.TryGetValue(dc, out long good) && good > Environment.TickCount64)
            return DirectTicket.Known;
        return _directProbe.TryAdd(dc, 0) ? DirectTicket.Probe : DirectTicket.Skip;
    }

    /// <summary>Report how the direct attempt went and release the probe. Cooling a failed path down is
    /// left to the caller, which owns the cooldown windows.</summary>
    public void EndDirect(int dc, DirectTicket ticket, bool ok)
    {
        if (ok) _directGoodUntil[dc] = Environment.TickCount64 + DirectGoodMs;
        else _directGoodUntil.TryRemove(dc, out _); // it just failed — the next connection must re-probe
        if (ticket == DirectTicket.Probe) _directProbe.TryRemove(dc, out _);
    }

    private static string Decode(string s)
    {
        if (!s.EndsWith(".com", StringComparison.Ordinal)) return s;
        string p = s[..^4];
        int n = p.Count(char.IsLetter);
        var sb = new StringBuilder(p.Length);
        foreach (char c in p)
        {
            if (char.IsLetter(c))
            {
                int b = c > '`' ? 97 : 65;
                sb.Append((char)(((c - b - n) % 26 + 26) % 26 + b));
            }
            else sb.Append(c);
        }
        return sb.ToString() + Suffix;
    }
}
