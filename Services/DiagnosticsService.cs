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

    /// <summary>Fresh set of rows for a run (grouped by service). Endpoints cover each
    /// service's real subsystems (login/API, gateway, CDN, media, video, bot-guard) plus
    /// the Cloudflare-ECH and Twitch-addon hosts the Flowseal lists target — so a fail in
    /// one cell points at the exact subsystem the provider is breaking.</summary>
    public static List<DiagRow> BuildRows() => new()
    {
        // Targets mirror Flowseal's blockcheck (его utils targets) — результаты ложатся 1:1 на то,
        // что сообщество считает «рабочим»: проще понять, что реально пробивается, а что нет.
        // ---- Discord ----
        new() { Group = "Discord",    Name = "Main (вход/API)",   Host = "discord.com" },
        new() { Group = "Discord",    Name = "Gateway (WS)",      Host = "gateway.discord.gg" },
        new() { Group = "Discord",    Name = "CDN",               Host = "cdn.discordapp.com" },
        new() { Group = "Discord",    Name = "Updates",           Host = "updates.discord.com" },
        // The Cloudflare bot-challenge the LOGIN page loads — if its HTTP cell fails, login won't pass.
        new() { Group = "Discord",    Name = "Вход (CF-челлендж)", Host = "challenges.cloudflare.com" },

        // ---- YouTube ----
        new() { Group = "YouTube",    Name = "Web",               Host = "www.youtube.com" },
        new() { Group = "YouTube",    Name = "Short",             Host = "youtu.be" },
        new() { Group = "YouTube",    Name = "Image (ytimg)",     Host = "i.ytimg.com" },
        new() { Group = "YouTube",    Name = "Video redirect",    Host = "redirector.googlevideo.com" },

        // ---- Cloudflare ----
        new() { Group = "Cloudflare", Name = "Web",               Host = "www.cloudflare.com" },
        new() { Group = "Cloudflare", Name = "CDN (cdnjs)",       Host = "cdnjs.cloudflare.com" },

        // ---- Google ----
        new() { Group = "Google",     Name = "Main",              Host = "www.google.com" },
        new() { Group = "Google",     Name = "Gstatic",           Host = "www.gstatic.com" },

        // ---- DNS (доступность резолверов, как у Flowseal) ----
        new() { Group = "DNS",        Name = "Cloudflare 1.1.1.1", Host = "1.1.1.1", PingOnly = true },
        new() { Group = "DNS",        Name = "Cloudflare 1.0.0.1", Host = "1.0.0.1", PingOnly = true },
        new() { Group = "DNS",        Name = "Google 8.8.8.8",     Host = "8.8.8.8", PingOnly = true },
        new() { Group = "DNS",        Name = "Google 8.8.4.4",     Host = "8.8.4.4", PingOnly = true },
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
        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            // Stop pressed mid-probe leaves cells stuck on "Running" (their await threw on cancel).
            // Give every unfinished cell a terminal state so the UI doesn't spin forever.
            foreach (var r in rows)
            {
                if (r.Ping == DiagStatus.Running) r.Ping = DiagStatus.Timeout;
                if (r.Http == DiagStatus.Running) r.Http = DiagStatus.Timeout;
                if (r.Tls12 == DiagStatus.Running) r.Tls12 = DiagStatus.Timeout;
                if (r.Tls13 == DiagStatus.Running) r.Tls13 = DiagStatus.Timeout;
            }
        }

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

        // HTTP cell = a real browser-like HTTP/2 request (ALPN, Chrome UA, marker-checked), not a bare
        // port-80 reachability check: it catches the case where TLS handshakes fine but the request
        // resets mid-stream or returns a stub (Discord/Cloudflare login).
        row.Http = DiagStatus.Running;
        row.Http = await NetProbe.ReachAsync(row.Host, ct);

        row.Tls12 = DiagStatus.Running;
        row.Tls12 = await NetProbe.TlsAsync(row.Host, SslProtocols.Tls12, ct);

        row.Tls13 = DiagStatus.Running;
        row.Tls13 = await NetProbe.TlsAsync(row.Host, SslProtocols.Tls13, ct);
    }
}
