using System.Net.Http;
using System.Text;
using System.Text.Json;
using Zapret2UI.Localization;
using Zapret2UI.Services.Network;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Fresh bridges from the Tor Project's own distributor, on demand.
///
/// <para><b>Why this is not scraping.</b> These are the endpoints Tor Browser's own «Connection Assist»
/// calls, and the one that matters answers without a CAPTCHA: <c>POST /moat/circumvention/settings</c>
/// with a country code returns the bridges BridgeDB currently hands to that country. Verified live for
/// <c>ru</c>: webtunnel, obfs4 and snowflake lines came back, no challenge asked. The CAPTCHA-guarded
/// <c>/moat/fetch</c> pool is a different thing and is deliberately not touched here — a machine
/// answering a CAPTCHA is exactly what it exists to prevent.</para>
///
/// <para><b>Why the answer repeats.</b> BridgeDB buckets by the asking address, so calling again from
/// the same connection returns the same lines — measured, three calls in a row, identical fingerprints.
/// It rotates over time, not on demand, which is why the button says «получить», not «получить
/// ещё».</para>
///
/// <para><b>Why snowflake is dropped.</b> It needs a second binary the bundle ships and we do not
/// unpack, and tor refuses a whole config over one plugin line it cannot run — the same trap conjure
/// set. Only what lyrebird speaks survives the filter.</para>
/// </summary>
internal static class BridgeDirectory
{
    /// <summary>Bridges chosen for one censored country. Answers without a CAPTCHA.</summary>
    internal const string SettingsUrl = "https://bridges.torproject.org/moat/circumvention/settings";

    /// <summary>The built-in list as it stands TODAY, which is the point: the copy inside the bundle was
    /// frozen when the bundle was built, and its obfs4 bridges are the ones users find dead.</summary>
    internal const string BuiltinUrl = "https://bridges.torproject.org/moat/circumvention/builtin";

    /// <summary>What lyrebird — the only pluggable transport we unpack — can actually run. A line of any
    /// other kind is dropped rather than written into a torrc that tor would then refuse whole.</summary>
    internal static readonly IReadOnlyList<string> Usable = new[] { "obfs4", "webtunnel" };

    /// <summary>Ask for bridges and hand back the lines, best source first.
    ///
    /// <para><paramref name="throughProxy"/> is the way in for a network that blocks torproject.org —
    /// which is most of the networks this is for. Same shape as the bundle download: through WARP when
    /// there is one, straight out otherwise.</para></summary>
    internal static async Task<(WarpResult Result, List<string> Bridges)> FetchAsync(
        string country, string throughProxy = "", CancellationToken ct = default)
    {
        string code = (country ?? "").Trim().ToLowerInvariant();
        if (code.Length != 2 || !code.All(char.IsAsciiLetterLower)) code = "";

        using HttpClient http = throughProxy.Length > 0
            ? DohHttp.CreateThrough(throughProxy, TimeSpan.FromSeconds(40))
            : DohHttp.Create(TimeSpan.FromSeconds(40));

        var found = new List<string>();
        string failure = "";

        // The country list first: those are the lines BridgeDB believes still work from there, and for
        // Russia that is where the WebTunnel bridges come from. An empty code lets the server decide from
        // the address asking — right when we are asking directly, wrong when we are asking through WARP,
        // which is why the caller names the country whenever it knows it.
        {
            string body0 = code.Length == 2 ? "{\"country\":\"" + code + "\"}" : "{}";
            var (ok, body) = await PostAsync(http, SettingsUrl, body0, ct).ConfigureAwait(false);
            if (ok) Merge(found, ExtractSettings(body));
            else failure = body;
        }

        // Then the built-ins, which cost nothing extra and are the half the bundle has let go stale.
        var (builtOk, builtBody) = await PostAsync(http, BuiltinUrl, "{}", ct).ConfigureAwait(false);
        if (builtOk) Merge(found, ExtractBuiltin(builtBody));
        else if (failure.Length == 0) failure = builtBody;

        if (found.Count > 0) return (WarpResult.Success(""), found);

        return (WarpResult.Fail(failure.Length > 0
            ? Loc.T("Не удалось получить мосты: {0}", failure)
            : Loc.T("Распределитель Tor ответил, но мостов подходящего типа в ответе не оказалось.")),
            found);
    }

    private static async Task<(bool Ok, string Body)> PostAsync(HttpClient http, string url, string json,
                                                                CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json");
            using HttpResponseMessage response = await http.PostAsync(url, content, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, body)
                : (false, Loc.T("сервер ответил {0}", (int)response.StatusCode));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>The country answer: <c>{"settings":[{"bridges":{"type":…,"bridge_strings":[…]}}]}</c>.
    /// Split out so the shape is pinned by a test instead of by a live server.</summary>
    internal static List<string> ExtractSettings(string json)
    {
        var lines = new List<string>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("settings", out JsonElement settings)) return lines;

            foreach (JsonElement entry in settings.EnumerateArray())
            {
                if (!entry.TryGetProperty("bridges", out JsonElement bridges)) continue;
                if (!bridges.TryGetProperty("bridge_strings", out JsonElement strings)) continue;

                foreach (JsonElement line in strings.EnumerateArray()) Take(lines, line.GetString());
            }
        }
        catch { /* an answer we cannot read is an answer with no bridges in it */ }
        return lines;
    }

    /// <summary>The built-in answer: <c>{"obfs4":[…],"snowflake":[…],"meek":[…]}</c>.</summary>
    internal static List<string> ExtractBuiltin(string json)
    {
        var lines = new List<string>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonProperty kind in doc.RootElement.EnumerateObject())
            {
                if (kind.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement line in kind.Value.EnumerateArray()) Take(lines, line.GetString());
            }
        }
        catch { /* same */ }
        return lines;
    }

    /// <summary>Keep a line only if lyrebird can run it and it is not already in hand.</summary>
    private static void Take(List<string> lines, string? raw)
    {
        string line = (raw ?? "").Trim();
        if (line.Length is 0 or > 512 || line.Any(char.IsControl)) return;

        string kind = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!Usable.Contains(kind, StringComparer.OrdinalIgnoreCase)) return;

        if (!lines.Contains(line, StringComparer.OrdinalIgnoreCase)) lines.Add(line);
    }

    private static void Merge(List<string> into, IEnumerable<string> more)
    {
        foreach (string line in more)
            if (!into.Contains(line, StringComparer.OrdinalIgnoreCase)) into.Add(line);
    }

    /// <summary>Which country to ask for bridges as. Asked of Cloudflare DIRECTLY and on purpose: the
    /// distributor's own guess comes from whatever address reaches it, so asking through WARP would fetch
    /// the bridges for wherever WARP came out instead of for where the user is sitting. Empty when the
    /// network will not answer, which leaves the server to guess — still better than guessing here.</summary>
    internal static async Task<string> DetectCountryAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpClient http = DohHttp.Create(TimeSpan.FromSeconds(12));
            string body = await http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct).ConfigureAwait(false);
            string code = (WarpTrace.Parse(body)?.Location ?? "").Trim().ToLowerInvariant();
            return code.Length == 2 && code.All(char.IsAsciiLetterLower) ? code : "";
        }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }

    /// <summary>obfs4's timing obfuscation, which its own author called «a substantial performance
    /// penalty for a dubious and poorly understood privacy gain»: the bridge inserts artificial delays
    /// between frames. Worth pointing at when the complaint is speed.</summary>
    internal static bool IsSlowByDesign(string bridge) =>
        bridge.Contains("iat-mode=1", StringComparison.OrdinalIgnoreCase) ||
        bridge.Contains("iat-mode=2", StringComparison.OrdinalIgnoreCase);
}
