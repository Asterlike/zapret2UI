using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Zapret2UI.Localization;
using Zapret2UI.Services.Network;

namespace Zapret2UI.Services.Warp;

/// <summary>
/// Brings each bridge up on its own and measures what it can actually carry.
///
/// <para><b>Why this is the feature and not a nicety.</b> The bridge is the one hop the user picks, and
/// picks blind: Tor itself never says a bridge is slow, it either connects or it does not. MEASURED, the
/// difference is large — one obfs4 bridge carried 3.2 Mbit/s, ordinary relays used as bridges 1.1 to
/// 20.6. (It is not the only ceiling: a single Tor stream is capped near 500 KB/s by its flow-control
/// window, see proposal 324 — which is why the chain also opens a second session once the exit is
/// pinned.)</para>
///
/// <para><b>Why one at a time.</b> Two bridges measured together share the line and both read low, which
/// is worse than not measuring: the user would drop a good bridge on the strength of a number the test
/// itself caused. So the bridges are taken in turn, and the price is the minutes it takes.</para>
///
/// <para><b>Why a time window rather than a fixed file.</b> A slow bridge and a fast one must cost the
/// same wait. The download runs for a fixed number of seconds and what arrived is the answer, so a dead
/// bridge cannot hold the test open for a megabyte that is never coming.</para>
/// </summary>
internal sealed class BridgeSpeedTest
{
    /// <summary>Big enough that the window always closes first, so the measurement is never cut short by
    /// the file ending.</summary>
    private const string Target = "https://speed.cloudflare.com/__down?bytes=52428800";

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(8);

    /// <summary>How long one bridge gets to come up. MEASURED: a healthy bridge bootstraps in 9–50
    /// seconds, and one that has not by then is not going to in the next minute either.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(70);

    /// <summary>What one bridge turned out to be worth.</summary>
    /// <param name="Bridge">The line exactly as the user has it.</param>
    /// <param name="Mbits">Measured throughput, 0 when it never came up.</param>
    /// <param name="FirstByte">Seconds from asking to the first byte — the part that decides how a page
    /// FEELS, and the part a single number for speed hides.</param>
    /// <param name="Note">Why it is not a number, when it is not.</param>
    internal readonly record struct Row(string Bridge, bool Ok, double Mbits, double FirstByte, string Note)
    {
        /// <summary>The comment line written above the bridge in the user's own box.</summary>
        internal string Comment => Ok
            ? Loc.T("# {0} Мбит/с, первый байт {1} с", Mbits.ToString("0.0"), FirstByte.ToString("0.0"))
            : "# " + Note;
    }

    internal event Action<string>? Status;

    internal event Action<string>? LogLine;

    /// <summary>Measure every bridge in turn. Never throws for a bridge that fails — a dead bridge is a
    /// result, not an error.</summary>
    internal async Task<List<Row>> RunAsync(IReadOnlyList<string> bridges, string country,
                                            CancellationToken ct = default)
    {
        var rows = new List<Row>();

        for (int i = 0; i < bridges.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string bridge = bridges[i];

            Status?.Invoke(Loc.T("Проверка моста {0} из {1}…", i + 1, bridges.Count));
            rows.Add(await MeasureAsync(bridge, country, ct).ConfigureAwait(false));

            Row last = rows[^1];
            LogLine?.Invoke(Loc.T("[мосты] {0} — {1}", Short(bridge),
                                  last.Ok ? Loc.T("{0} Мбит/с", last.Mbits.ToString("0.0")) : last.Note));
        }

        return rows;
    }

    private async Task<Row> MeasureAsync(string bridge, string country, CancellationToken ct)
    {
        using var tor = new TorRuntime();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Patience);

        try
        {
            WarpResult up = await tor.StartAsync(country, bridge, "", "", deadline.Token).ConfigureAwait(false);
            if (!up.Ok)
                return new Row(bridge, false, 0, 0, Loc.T("не поднялся"));

            var (bytes, seconds, firstByte) = await PullAsync(tor.SocksPort, ct).ConfigureAwait(false);
            if (bytes == 0)
                return new Row(bridge, false, 0, 0, Loc.T("поднялся, но ничего не передал"));

            double mbits = bytes * 8 / seconds / 1e6;
            string note = BridgeDirectory.IsSlowByDesign(bridge)
                ? Loc.T("iat-mode замедляет этот мост намеренно")
                : "";
            return new Row(bridge, true, mbits, firstByte, note);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Row(bridge, false, 0, 0, Loc.T("не поднялся за {0} с", (int)Patience.TotalSeconds));
        }
        catch (Exception ex)
        {
            return new Row(bridge, false, 0, 0, ex.Message);
        }
        finally
        {
            tor.Stop();
        }
    }

    /// <summary>Read for the length of the window and report what arrived.</summary>
    private static async Task<(long Bytes, double Seconds, double FirstByte)> PullAsync(
        int socksPort, CancellationToken ct)
    {
        using HttpClient http = DohHttp.CreateThrough(
            "127.0.0.1:" + socksPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Window + TimeSpan.FromSeconds(20));

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(Window + TimeSpan.FromSeconds(20));   // the read loop closes itself below

        var clock = Stopwatch.StartNew();
        double firstByte = 0;
        long total = 0;

        try
        {
            using Stream stream = await http.GetStreamAsync(Target, window.Token).ConfigureAwait(false);
            byte[] buffer = new byte[64 * 1024];

            // The clock for the RATE starts at the first byte, not at the request: the seconds spent
            // building a circuit belong to «first byte», and counting them twice would report a fast
            // bridge behind a slow handshake as a slow bridge.
            int read = await stream.ReadAsync(buffer, window.Token).ConfigureAwait(false);
            if (read <= 0) return (0, 1, clock.Elapsed.TotalSeconds);

            firstByte = clock.Elapsed.TotalSeconds;
            total = read;
            var rate = Stopwatch.StartNew();

            while (rate.Elapsed < Window)
            {
                read = await stream.ReadAsync(buffer, window.Token).ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
            }

            rate.Stop();
            return (total, Math.Max(rate.Elapsed.TotalSeconds, 0.001), firstByte);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (total, Math.Max(clock.Elapsed.TotalSeconds - firstByte, 0.001), firstByte);
        }
        catch
        {
            return (0, 1, firstByte);
        }
    }

    /// <summary>Enough of a bridge line to recognise it in the journal, without the certificate.</summary>
    internal static string Short(string bridge)
    {
        string[] parts = bridge.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + " " + parts[1] : bridge;
    }

    /// <summary>The user's box, rewritten: fastest first, each line carrying what it measured, and
    /// nothing thrown away — a bridge that failed today is a bridge that may answer tomorrow, so it sinks
    /// to the bottom with the reason instead of disappearing.</summary>
    internal static string Rewrite(IReadOnlyList<Row> rows)
    {
        var text = new System.Text.StringBuilder();
        foreach (Row row in rows.OrderByDescending(r => r.Ok).ThenByDescending(r => r.Mbits))
        {
            text.Append(row.Comment).Append('\n');
            if (row.Note.Length > 0 && row.Ok) text.Append("# ").Append(row.Note).Append('\n');
            text.Append(row.Bridge).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }
}
