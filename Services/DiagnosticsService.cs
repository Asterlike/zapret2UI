using System.Security.Authentication;
using ZapretUI.Models;

namespace ZapretUI.Services;

/// <summary>
/// Endpoint-matrix diagnostics (CDPIUI/blockcheck-style). For a curated list of
/// named endpoints it probes HTTP (port 80), a TLS 1.2 handshake and a TLS 1.3
/// handshake (port 443, real SNI) plus a latency ping, and reports OK / ОШИБКА /
/// Таймаут per cell. It tests whatever is currently on the wire — start a preset
/// first to see how that bypass performs.
/// </summary>
public sealed class DiagnosticsService
{
    public event Action<string>? Status;

    /// <summary>Fresh set of rows for a run (grouped by service).</summary>
    public static List<DiagRow> BuildRows() => new()
    {
        new() { Group = "Discord",    Name = "Main (вход/API)",  Host = "discord.com" },
        new() { Group = "Discord",    Name = "Gateway",          Host = "gateway.discord.gg" },
        new() { Group = "Discord",    Name = "CDN",              Host = "cdn.discordapp.com" },
        new() { Group = "Discord",    Name = "Media",            Host = "discord.media" },

        new() { Group = "YouTube",    Name = "Web",              Host = "www.youtube.com" },
        new() { Group = "YouTube",    Name = "Image (ytimg)",    Host = "i.ytimg.com" },
        new() { Group = "YouTube",    Name = "Video",            Host = "rr1---sn-axq.googlevideo.com" },

        // Telegram — SNI/web part (TLS-probeable, fixable by zapret):
        new() { Group = "Telegram",   Name = "Web-клиент",       Host = "web.telegram.org" },
        new() { Group = "Telegram",   Name = "API приложения",   Host = "api.telegram.org" },
        new() { Group = "Telegram",   Name = "Сайт/ссылки",      Host = "t.me" },
        // MTProto DC by IP — reachability only (no TLS/SNI; throttle isn't measurable here):
        new() { Group = "Telegram",   Name = "DC2 MTProto (IP)", Host = "149.154.167.50", TcpOnly = true },

        new() { Group = "Google",     Name = "Main",             Host = "www.google.com" },
        new() { Group = "Google",     Name = "Gstatic",          Host = "www.gstatic.com" },

        new() { Group = "Cloudflare", Name = "Web",              Host = "www.cloudflare.com" },
        new() { Group = "Cloudflare", Name = "CDN (cdnjs)",      Host = "cdnjs.cloudflare.com" },

        new() { Group = "DNS",        Name = "Cloudflare 1.1.1.1", Host = "1.1.1.1", PingOnly = true },
        new() { Group = "DNS",        Name = "Google 8.8.8.8",     Host = "8.8.8.8", PingOnly = true },
        new() { Group = "DNS",        Name = "Quad9 9.9.9.9",      Host = "9.9.9.9", PingOnly = true },
    };

    public async Task RunAsync(IReadOnlyList<DiagRow> rows, CancellationToken ct)
    {
        foreach (var r in rows) r.Reset();
        Status?.Invoke("Диагностика запущена…");

        int done = 0, total = rows.Count;
        using var gate = new SemaphoreSlim(6);
        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await ProbeRowAsync(row, ct);
                int n = Interlocked.Increment(ref done);
                Status?.Invoke($"Проверено {n}/{total}…");
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        ct.ThrowIfCancellationRequested();
        Status?.Invoke("Диагностика завершена.");
    }

    private static async Task ProbeRowAsync(DiagRow row, CancellationToken ct)
    {
        // Ping for everyone.
        row.Ping = DiagStatus.Running;
        var (pOk, pMs) = await NetProbe.PingAsync(row.Host, ct);
        row.PingText = pOk ? $"{pMs} мс" : "таймаут";
        row.Ping = pOk ? DiagStatus.Ok : DiagStatus.Timeout;

        if (row.PingOnly) return;

        // MTProto DC IP: a plain TCP-connect on 443 = reachability (no HTTP/TLS to speak).
        if (row.TcpOnly)
        {
            row.Http = DiagStatus.Running;
            row.Http = await NetProbe.TcpAsync(row.Host, 443, ct);
            return;
        }

        row.Http = DiagStatus.Running;
        row.Http = await NetProbe.HttpAsync(row.Host, ct);

        row.Tls12 = DiagStatus.Running;
        row.Tls12 = await NetProbe.TlsAsync(row.Host, SslProtocols.Tls12, ct);

        row.Tls13 = DiagStatus.Running;
        row.Tls13 = await NetProbe.TlsAsync(row.Host, SslProtocols.Tls13, ct);
    }
}
