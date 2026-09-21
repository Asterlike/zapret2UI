using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using Zapret2UI.Localization;
using Zapret2UI.Services.Network;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Ordinary Tor relays used as the way IN, for networks where the published bridge lists are already
/// blocked.
///
/// <para><b>Why a plain relay can be a bridge.</b> <c>UseBridges 1</c> plus <c>Bridge address:port
/// fingerprint</c> takes any relay, not only the ones BridgeDB hands out — that is the idea behind
/// ValdikSS's tor-relay-scanner. There are nearly five thousand usable guards in the consensus and no
/// list of them is distributed to censors relay by relay, so the ones that answer from a given network
/// are found by asking rather than by looking them up.</para>
///
/// <para><b>Why it can be FASTER than obfs4 or WebTunnel.</b> A plain relay needs no pluggable
/// transport: no obfs4 framing, no HTTPS wrapper, no lyrebird process in the path. Latency is what caps
/// a Tor stream — proposal 324 puts a single stream at about 500 KB/s divided by the circuit's
/// round-trip — so taking work out of the first hop is one of the few levers that moves it. The honest
/// cost is that the connection looks like Tor to anything doing protocol analysis, where obfs4 does
/// not.</para>
///
/// <para><b>Why the addresses are probed rather than trusted.</b> Any relay is reachable from somewhere;
/// the question is whether it is reachable from HERE. A dead line in a torrc costs bootstrap time and
/// tells the user nothing, so only addresses that completed a TLS handshake with this machine, just now,
/// are written into the box — see <see cref="AnswersAsync"/> for why a bare connect is not proof.</para>
/// </summary>
internal static class RelayScanner
{
    /// <summary>Tor's own directory service, which is also what tor-relay-scanner asks. Ordered by
    /// consensus weight so the pool is made of relays with capacity, and capped: the whole answer is
    /// megabytes, and most of what is past the first few hundred is too small to carry a tunnel.</summary>
    internal const string OnionooUrl =
        "https://onionoo.torproject.org/details?type=relay&running=true"
        + "&fields=fingerprint,or_addresses&order=-consensus_weight&limit=600";

    /// <summary>How many addresses to try before giving up on the sweep. Each is one handshake with a
    /// short deadline, many at a time, so this is seconds rather than minutes.</summary>
    internal const int Attempts = 120;

    /// <summary>How long one address gets to answer. A relay slower than this from here is a relay whose
    /// latency would cap the tunnel anyway.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>One relay, in the shape a torrc wants it.</summary>
    internal readonly record struct Relay(string Address, int Port, string Fingerprint)
    {
        /// <summary>The value of a <c>Bridge</c> line — no transport name, because there is no
        /// transport.</summary>
        internal string Line => Address + ":"
            + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Fingerprint;
    }

    /// <summary>Find relays this machine can actually reach, best source first.
    ///
    /// <para>The cached consensus is preferred over the network: it is already on disk, it is the same
    /// data, and it cannot be blocked. Onionoo is the fallback for the case that needs it most — someone
    /// who has never managed to bootstrap and therefore has no consensus at all.</para></summary>
    internal static async Task<(WarpResult Result, List<string> Bridges)> FindAsync(
        int take, string throughProxy = "", CancellationToken ct = default)
    {
        List<Relay> pool = FromConsensus(ExitDirectory.ConsensusFile);
        string failure = "";

        if (pool.Count == 0)
        {
            var (ok, body) = await AskOnionooAsync(throughProxy, ct).ConfigureAwait(false);
            if (ok) pool = ExtractOnionoo(body);
            else failure = body;
        }

        if (pool.Count == 0)
            return (WarpResult.Fail(failure.Length > 0
                ? Loc.T("Не удалось получить список реле Tor: {0}", failure)
                : Loc.T("Список реле Tor пуст.")), new List<string>());

        List<Relay> alive = await ProbeAsync(Shuffle(pool), take, ct).ConfigureAwait(false);
        return alive.Count > 0
            ? (WarpResult.Success(""), alive.Select(r => r.Line).ToList())
            : (WarpResult.Fail(Loc.T("Ни одно из проверенных реле Tor отсюда не отвечает.")),
               new List<string>());
    }

    /// <summary>The relays in tor's own cached consensus worth dialling: fast, stable, running, and
    /// allowed to be somebody's first hop.
    ///
    /// <para>The <c>r</c> line of a microdescriptor consensus is
    /// <c>r nickname identity published time address orport dirport</c>, and the identity is the
    /// fingerprint in base64 — which is why nothing has to be looked up to write a <c>Bridge</c>
    /// line.</para></summary>
    internal static List<Relay> FromConsensus(string consensusPath)
    {
        var found = new List<Relay>();
        string identity = "", address = "";
        int port = 0;

        try
        {
            foreach (string line in File.ReadLines(consensusPath))
            {
                if (line.StartsWith("r ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    identity = parts.Length > 6 ? parts[2] : "";
                    address = parts.Length > 6 ? ExitDirectory.SanitiseAddress(parts[5]) : "";
                    port = parts.Length > 6 && int.TryParse(parts[6], out int p) ? p : 0;
                }
                else if (line.StartsWith("s ", StringComparison.Ordinal))
                {
                    var flags = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // Guard is the flag that decides it: a relay without it is not accepted as a first
                    // hop, so a Bridge line naming one is a line tor will not use.
                    if (flags.Contains("Guard") && flags.Contains("Running")
                        && flags.Contains("Stable") && flags.Contains("Fast"))
                        Take(found, address, port, Fingerprint(identity));

                    identity = address = "";
                    port = 0;
                }
            }
        }
        catch { /* no consensus yet: the caller falls back to onionoo */ }

        return found;
    }

    /// <summary>Onionoo's answer: <c>{"relays":[{"fingerprint":…,"or_addresses":["1.2.3.4:9001",…]}]}</c>.
    /// Split out so its shape is pinned by a test rather than by a live server.</summary>
    internal static List<Relay> ExtractOnionoo(string json)
    {
        var found = new List<Relay>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("relays", out JsonElement relays)) return found;

            foreach (JsonElement relay in relays.EnumerateArray())
            {
                if (!relay.TryGetProperty("fingerprint", out JsonElement fp)) continue;
                if (!relay.TryGetProperty("or_addresses", out JsonElement addresses)) continue;

                string print = Hex(fp.GetString());
                foreach (JsonElement entry in addresses.EnumerateArray())
                {
                    // IPv6 comes back as «[2001:db8::1]:9001» and is skipped: everything below this runs
                    // over IPv4, and a bridge tor cannot reach only costs time.
                    string text = entry.GetString() ?? "";
                    int mark = text.LastIndexOf(':');
                    if (mark <= 0 || text.Contains('[')) continue;

                    Take(found, ExitDirectory.SanitiseAddress(text[..mark]),
                         int.TryParse(text.AsSpan(mark + 1), out int p) ? p : 0, print);
                }
            }
        }
        catch { /* an answer we cannot read is an answer with no relays in it */ }
        return found;
    }

    /// <summary>Which of these answer from this machine — the first <paramref name="take"/> that do.
    ///
    /// <para>Run wide rather than one at a time: the sweep is almost entirely waiting, and a hundred
    /// waits at once take as long as the slowest one instead of as long as all of them.</para></summary>
    internal static async Task<List<Relay>> ProbeAsync(IReadOnlyList<Relay> pool, int take,
                                                       CancellationToken ct = default)
    {
        var alive = new List<Relay>();
        if (take <= 0) return alive;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(24);
        var guard = new object();

        var running = new List<Task>();
        foreach (Relay relay in pool.Take(Attempts))
        {
            Relay it = relay;
            running.Add(Task.Run(async () =>
            {
                try
                {
                    await gate.WaitAsync(stop.Token).ConfigureAwait(false);
                    try
                    {
                        if (!await AnswersAsync(it, stop.Token).ConfigureAwait(false)) return;
                    }
                    finally { gate.Release(); }

                    lock (guard)
                    {
                        if (alive.Count >= take) return;
                        alive.Add(it);
                        // Enough found: everything still waiting is waiting for nothing.
                        if (alive.Count >= take) stop.Cancel();
                    }
                }
                catch (OperationCanceledException) { /* the sweep is over */ }
            }, CancellationToken.None));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        lock (guard) return alive.Take(take).ToList();
    }

    /// <summary>Does a relay answer from here — proved by a TLS handshake rather than by a connect.
    ///
    /// <para><b>Why a connect is not enough.</b> MEASURED on the owner's machine: a TCP connect to
    /// <c>192.0.2.1</c>, <c>203.0.113.9</c> and even <c>10.255.255.1</c> all succeeded in under 30 ms —
    /// addresses reserved for documentation and routed nowhere. Something in the path (a VPN client, a
    /// transparent proxy) accepts on behalf of every destination, and against that a connect says yes to
    /// everything, which would fill the box with dead bridges. A Tor relay speaks TLS on its ORPort, so
    /// asking for a handshake tells the two apart: the same measurement gave TLS 1.3 in 0.17 s from a real
    /// relay, and an immediate EOF from the addresses that were not there.</para>
    ///
    /// <para><b>Why the certificate is not checked.</b> A relay presents a self-signed certificate with a
    /// made-up name, so there is nothing here to validate against — and nothing to validate FOR. This
    /// asks «is a Tor relay listening», not «is it the right one»; identity is tor's own job, done
    /// against the fingerprint on the <c>Bridge</c> line, and no traffic is ever sent through this
    /// connection.</para></summary>
    private static async Task<bool> AnswersAsync(Relay relay, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Patience);

        try
        {
            await client.ConnectAsync(IPAddress.Parse(relay.Address), relay.Port, deadline.Token)
                        .ConfigureAwait(false);

            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false,
                                          (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = relay.Address,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates
                                                       .X509RevocationMode.NoCheck,
            }, deadline.Token).ConfigureAwait(false);

            return true;
        }
        catch { return false; }
    }

    private static async Task<(bool Ok, string Body)> AskOnionooAsync(string throughProxy,
                                                                      CancellationToken ct)
    {
        try
        {
            using HttpClient http = throughProxy.Length > 0
                ? DohHttp.CreateThrough(throughProxy, TimeSpan.FromSeconds(40))
                : DohHttp.Create(TimeSpan.FromSeconds(40));
            return (true, await http.GetStringAsync(OnionooUrl, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>The pool in a different order every time, with the relays listening on 443 in front.
    ///
    /// <para>Shuffled because the widest relays are also the best known, and «best known» is the first
    /// thing a block list gets; the point of this is the address nobody bothered to list. Port 443 first
    /// because on a network that filters outbound ports it is the one that passes for an ordinary HTTPS
    /// connection.</para></summary>
    internal static List<Relay> Shuffle(IReadOnlyList<Relay> pool)
    {
        var order = pool.ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order.OrderBy(r => r.Port == 443 ? 0 : 1).ToList();
    }

    /// <summary>A relay worth keeping, once. Every field here becomes part of one line of a torrc, so an
    /// address or a fingerprint that is not exactly what it should be is dropped rather than written
    /// out.</summary>
    private static void Take(List<Relay> into, string address, int port, string fingerprint)
    {
        if (address.Length == 0 || port is < 1 or > 65535 || fingerprint.Length != 40) return;
        if (into.Any(r => string.Equals(r.Address, address, StringComparison.Ordinal))) return;
        into.Add(new Relay(address, port, fingerprint));
    }

    /// <summary>The consensus stores an identity as unpadded base64 of the twenty-byte fingerprint; a
    /// torrc wants the same thing as forty hex characters.</summary>
    internal static string Fingerprint(string? identity)
    {
        string value = (identity ?? "").Trim();
        if (value.Length != 27) return "";
        try
        {
            byte[] raw = Convert.FromBase64String(value + "=");
            return raw.Length == 20 ? Convert.ToHexString(raw) : "";
        }
        catch { return ""; }
    }

    private static string Hex(string? text)
    {
        string value = (text ?? "").Trim().ToUpperInvariant();
        return value.Length == 40 && value.All(char.IsAsciiHexDigitUpper) ? value : "";
    }
}
