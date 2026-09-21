using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using Zapret2UI.Localization;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Finds which of the country's fastest exits is actually fastest from HERE, and hands back the address
/// to pin.
///
/// <para><b>Why measure at all, when the consensus already ranks them.</b> The consensus bandwidth is
/// what a relay managed for everybody, averaged over days; what matters here is what it manages for one
/// user, through one bridge, right now. The ranking narrows 419 candidates to ten in milliseconds and
/// the measurement decides between those ten — neither half works alone.</para>
///
/// <para><b>Why one Tor and many circuits.</b> Forcing a particular exit means rewriting the config and
/// restarting, which is a minute each. Instead tor is started once with all ten candidates allowed, and
/// separate SOCKS logins buy separate circuits — tor isolates by credential — so each sample lands on
/// whichever candidate it lands on and reports which. Sampling rather than enumerating: the cost is that
/// a candidate may never come up, and that is cheaper than ten restarts.</para>
///
/// <para><b>What it cannot control.</b> Every sample shares the bridge but gets its own middle relay, so
/// the numbers carry noise that belongs to the path and not to the exit. Ranking by consensus bandwidth
/// first is what keeps that noise from choosing a genuinely slow exit.</para>
/// </summary>
internal sealed class ExitPicker
{
    private const string Target = "https://speed.cloudflare.com/__down?bytes=52428800";

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(8);

    /// <summary>How many of the consensus's best to allow, and how many circuits to open. More circuits
    /// than candidates on purpose: the draw is weighted, so some candidates need a second chance to come
    /// up at all.</summary>
    private const int Candidates = 10;

    private const int Samples = 12;

    /// <summary>One exit, as it behaved here.</summary>
    internal readonly record struct Row(string Address, double Mbits);

    internal event Action<string>? Status;

    internal event Action<string>? LogLine;

    /// <summary>Measure the country's best exits and return the address worth pinning.</summary>
    internal async Task<(WarpResult Result, string Address, List<Row> Rows)> RunAsync(
        string country, string bridgesText, CancellationToken ct = default)
    {
        var rows = new List<Row>();

        var ranked = ExitDirectory.Rank(country, Candidates);
        if (ranked.Count == 0)
            return (WarpResult.Fail(Loc.T(
                "Не из чего выбирать: список узлов Tor ещё не скачан. Включите цепочку один раз — "
                + "Tor заберёт его сам, — и повторите.")), "", rows);

        var byAddress = ranked.ToDictionary(c => c.Address, StringComparer.Ordinal);
        LogLine?.Invoke(Loc.T("[выход] кандидатов из консенсуса: {0}, лучший по полосе {1}",
                              ranked.Count, ranked[0].Nickname));

        using var tor = new TorRuntime();
        tor.LogLine += line => LogLine?.Invoke(line);

        Status?.Invoke(Loc.T("Запуск Tor для замера выходов…"));
        WarpResult up = await tor.StartAsync(country, bridgesText, "",
                                             string.Join(",", ranked.Select(c => c.Address)), ct)
                                 .ConfigureAwait(false);
        if (!up.Ok) return (up, "", rows);

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 1; i <= Samples && seen.Count < ranked.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Status?.Invoke(Loc.T("Замер выходов: проба {0} из {1}, найдено {2}…", i, Samples, seen.Count));

                // A fresh login means a fresh circuit, so each pass may land on a different candidate.
                using HttpClient http = Through(tor.SocksPort, "pick" + i.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

                string address = await ExitAddressAsync(http, ct).ConfigureAwait(false);
                if (address.Length == 0 || !seen.Add(address)) continue;

                double mbits = await PullAsync(http, ct).ConfigureAwait(false);
                byAddress.TryGetValue(address, out ExitDirectory.Candidate known);

                rows.Add(new Row(address, mbits));
                LogLine?.Invoke(Loc.T("[выход] {0} ({1}) — {2} Мбит/с", address,
                                      known.Nickname ?? "?", mbits.ToString("0.0")));
            }
        }
        finally { tor.Stop(); }

        if (rows.Count == 0)
            return (WarpResult.Fail(Loc.T(
                "Ни один из выбранных выходов не ответил. Обычно это мост: проверьте его скорость.")), "", rows);

        rows.Sort((a, b) => b.Mbits.CompareTo(a.Mbits));
        return (WarpResult.Success(""), rows[0].Address, rows);
    }

    /// <summary>A client whose every connection goes through one circuit of its own: tor gives a separate
    /// circuit per SOCKS login, which is how several exits get sampled without restarting anything.</summary>
    private static HttpClient Through(int socksPort, string login)
    {
        var proxy = new WebProxy(new Uri("socks5://127.0.0.1:"
            + socksPort.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            Credentials = new NetworkCredential(login, "x"),
        };

        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(25),
        })
        { Timeout = TimeSpan.FromSeconds(40) };
    }

    /// <summary>Which exit this circuit came out of — Cloudflare's own answer, so it is the address the
    /// far side really sees rather than one we inferred.</summary>
    private static async Task<string> ExitAddressAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            string body = await http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace", ct).ConfigureAwait(false);
            return ExitDirectory.SanitiseAddress(WarpTrace.Parse(body)?.Ip ?? "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return ""; }
    }

    /// <summary>Read for the length of the window and report the rate. Same shape as the bridge test, and
    /// deliberately so: the two numbers are compared by the same person on the same screen.</summary>
    private static async Task<double> PullAsync(HttpClient http, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(Window + TimeSpan.FromSeconds(20));

        long total = 0;
        var clock = new Stopwatch();

        try
        {
            using Stream stream = await http.GetStreamAsync(Target, window.Token).ConfigureAwait(false);
            byte[] buffer = new byte[64 * 1024];

            int read = await stream.ReadAsync(buffer, window.Token).ConfigureAwait(false);
            if (read <= 0) return 0;

            total = read;
            clock.Start();
            while (clock.Elapsed < Window)
            {
                read = await stream.ReadAsync(buffer, window.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* whatever arrived before it broke is still the answer */ }

        double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        return total * 8 / seconds / 1e6;
    }
}
