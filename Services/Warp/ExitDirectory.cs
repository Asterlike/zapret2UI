using System.IO;
using System.Net;
using Zapret2UI.Services.Infrastructure;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Which Tor exit to come out of, read from the files Tor already keeps on disk.
///
/// <para><b>Why an exit needs choosing at all.</b> <c>ExitNodes {de}</c> means «any of the 419 German
/// exits», and Tor draws a fresh one for every circuit. Each draw is a different address, so every
/// reconnect hands Cloudflare a different address to dial from and the user a different WARP exit — a new
/// address arriving at the same account, over and over, which is exactly the pattern services flag. Pin
/// one and the address stops moving.</para>
///
/// <para><b>Why by ADDRESS and not by fingerprint.</b> What decides the WARP exit is the address the
/// connection is dialled from, so the address is the thing worth pinning. It is also the more robust of
/// the two: big operators run many relays behind one address — measured in this consensus, sixteen
/// F3Netze instances across <c>185.220.100.240-255</c> — so an address keeps working when one instance
/// goes down, where a fingerprint does not. <c>ExitNodes</c> takes «identity fingerprints, country codes,
/// and address patterns», so both are legal; this is the one that survives.</para>
///
/// <para><b>Why nothing is downloaded.</b> Tor's own cached consensus carries every relay's address,
/// flags and measured bandwidth, and the bundle's geoip file maps an address to a country. Both are
/// already on disk beside tor.exe, so choosing an exit needs no network and no third-party service.</para>
/// </summary>
internal static class ExitDirectory
{
    /// <summary>A relay worth measuring.</summary>
    /// <param name="Address">Its IPv4 address — what gets pinned.</param>
    /// <param name="Nickname">For the journal, so a line means something to a human.</param>
    /// <param name="Bandwidth">The consensus's own measurement, in its own units. Comparable between
    /// relays and nothing more, which is all it is used for.</param>
    internal readonly record struct Candidate(string Address, string Nickname, long Bandwidth);

    internal static string ConsensusFile => Path.Combine(AppPaths.TorDataDir, "cached-microdesc-consensus");

    /// <summary>The fastest exits in one country, one entry per ADDRESS, best first.
    ///
    /// <para>Ranked by the consensus bandwidth before anything is measured, because measuring is minutes
    /// and this is milliseconds: it turns «try 419 relays» into «try the ten that could plausibly win».
    /// Measured on a live consensus: 419 German exits, median 37 000, and the top tenth averaging 2.6×
    /// that — so the pre-filter is most of the work.</para></summary>
    internal static List<Candidate> Rank(string country, int take = 10)
    {
        var best = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        try
        {
            var geo = GeoIp.Load(AppPaths.TorGeoIpFile);
            string want = TorRuntime.SanitiseCountry(country);
            if (want.Length == 0 || geo.Count == 0) return new List<Candidate>();

            foreach (Candidate relay in ReadExits(ConsensusFile))
            {
                if (!string.Equals(geo.Country(relay.Address), want, StringComparison.Ordinal)) continue;

                // One row per address: several relays often share one, and their bandwidths are their
                // own — the address is only as good as the best thing answering on it.
                if (!best.TryGetValue(relay.Address, out Candidate held) || relay.Bandwidth > held.Bandwidth)
                    best[relay.Address] = relay;
            }
        }
        catch { /* no consensus yet, or a file we cannot read: the caller falls back to the country */ }

        return best.Values.OrderByDescending(c => c.Bandwidth).Take(Math.Max(take, 0)).ToList();
    }

    /// <summary>Every running exit in the cached consensus.
    ///
    /// <para>The format is Tor's own: an <c>r</c> line opens a relay and carries its address, <c>s</c>
    /// carries the flags and <c>w</c> the measured bandwidth. Only relays that are <c>Exit</c>,
    /// <c>Running</c> AND <c>Stable</c> are kept — an exit that is not stable is one the pin would
    /// outlive by minutes.</para></summary>
    internal static IEnumerable<Candidate> ReadExits(string consensusPath)
    {
        string nickname = "", address = "";
        bool usable = false;

        foreach (string line in File.ReadLines(consensusPath))
        {
            if (line.StartsWith("r ", StringComparison.Ordinal))
            {
                // r <nickname> <identity> <published date> <time> <address> <orport> <dirport>
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                nickname = parts.Length > 1 ? parts[1] : "";
                address = parts.Length > 6 ? SanitiseAddress(parts[5]) : "";
                usable = false;
            }
            else if (line.StartsWith("s ", StringComparison.Ordinal))
            {
                var flags = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                usable = flags.Contains("Exit") && flags.Contains("Running") && flags.Contains("Stable");
            }
            else if (line.StartsWith("w ", StringComparison.Ordinal) && usable && address.Length > 0)
            {
                long bandwidth = 0;
                foreach (string kv in line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (kv.StartsWith("Bandwidth=", StringComparison.Ordinal)
                        && long.TryParse(kv.AsSpan(10), out long value))
                        bandwidth = value;

                yield return new Candidate(address, nickname, bandwidth);
                usable = false;
            }
        }
    }

    /// <summary>An address that is safe to write into a torrc, or empty. Everything here ends up in a
    /// config file tor parses line by line, so «looks like an address» is not good enough — a value with
    /// a space in it would start a directive of its own.</summary>
    internal static string SanitiseAddress(string? text)
    {
        string value = (text ?? "").Trim();
        return IPAddress.TryParse(value, out IPAddress? parsed)
               && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? parsed.ToString()
            : "";
    }

    /// <summary>Several addresses, comma-separated, with anything unparseable dropped. Used both for the
    /// single pin and for the candidate set the picker measures.</summary>
    internal static string SanitiseAddressList(string? text)
    {
        var good = new List<string>();
        foreach (string part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string address = SanitiseAddress(part);
            if (address.Length > 0 && !good.Contains(address, StringComparer.Ordinal)) good.Add(address);
        }
        return string.Join(",", good);
    }

    /// <summary>What <c>ExitNodes</c> should say: the pinned address or addresses when there are any, the
    /// country otherwise. A pin without a country is still written — an address decides the country by
    /// itself, and writing both would only let them contradict each other.</summary>
    internal static string ExitNodesValue(string country, string pinnedAddress)
    {
        string pin = SanitiseAddressList(pinnedAddress);
        if (pin.Length > 0) return pin;

        string code = TorRuntime.SanitiseCountry(country);
        return code.Length > 0 ? "{" + code + "}" : "";
    }

    /// <summary>The bundle's geoip file: sorted ranges of IPv4 addresses, each with a country.</summary>
    internal sealed class GeoIp
    {
        private readonly List<uint> _from = new();
        private readonly List<uint> _to = new();
        private readonly List<string> _code = new();

        internal int Count => _from.Count;

        internal static GeoIp Load(string path)
        {
            var map = new GeoIp();
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] parts = line.Split(',');
                    if (parts.Length != 3) continue;
                    if (!uint.TryParse(parts[0], out uint from) || !uint.TryParse(parts[1], out uint to))
                        continue;

                    map._from.Add(from);
                    map._to.Add(to);
                    map._code.Add(parts[2].Trim().ToLowerInvariant());
                }
            }
            catch { /* a geoip we cannot read leaves every relay countryless */ }
            return map;
        }

        /// <summary>The country of an address, or empty. Binary search: the file is already sorted, and
        /// it holds nearly four hundred thousand ranges.</summary>
        internal string Country(string address)
        {
            if (!IPAddress.TryParse(address, out IPAddress? parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return "";

            byte[] octets = parsed.GetAddressBytes();
            uint value = ((uint)octets[0] << 24) | ((uint)octets[1] << 16)
                       | ((uint)octets[2] << 8) | octets[3];

            int index = _from.BinarySearch(value);
            if (index < 0) index = ~index - 1;
            return index >= 0 && index < _from.Count && value <= _to[index] ? _code[index] : "";
        }
    }
}
