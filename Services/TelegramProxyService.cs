using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ZapretUI.Services;

/// <summary>
/// Built-in local Telegram MTProto → WebSocket proxy (native C# port of Flowseal/tg-ws-proxy, MIT).
/// Telegram Desktop connects to it as an MTProto proxy on 127.0.0.1; the service relays each
/// connection to Telegram's data centers over WebSocket-TLS via Cloudflare-fronted domains, which
/// survives IP-based throttling/blocking the packet-desync engine cannot fix on its own.
///
/// No admin rights are required (a loopback listener + outbound TLS), so unlike the winws2 engine
/// this can run and be validated from a normal user session.
/// </summary>
public sealed class TelegramProxyService : IDisposable
{
    private const int MaxUpstreamAttempts = 6;

    // Keepalive: ping the upstream every 30 s so dead half-open sockets surface (the ping write fails)
    // and idle CF/edge intermediaries don't drop the tunnel. If nothing crosses in either direction for
    // 6 min (well past MTProto's own ~75 s transport ping) the bridge is torn down as a backstop.
    private const int KeepaliveMs = 30_000;
    private const int IdleTimeoutMs = 360_000;

    public string Host { get; } = "127.0.0.1";
    public int Port { get; private set; } = 1443;

    private byte[] _secret = TgProxyProto.RandomBytes(16);
    public string SecretHex => Convert.ToHexString(_secret).ToLowerInvariant();

    /// <summary>The tg:// deep link that configures Telegram Desktop's proxy in one click.</summary>
    public string ProxyLink => $"tg://proxy?server={Host}&port={Port}&secret=dd{SecretHex}";

    public bool IsRunning { get; private set; }

    /// <summary>Human-readable reason the last <see cref="Start"/> failed (e.g. the port is busy), or
    /// null on success. The UI shows it on the Telegram card while the proxy is off.</summary>
    public string? StartError { get; private set; }

    /// <summary>Raised for user-facing log lines (subscriber marshals to the UI thread).</summary>
    public event Action<string>? LogLine;
    /// <summary>Raised when <see cref="IsRunning"/> flips.</summary>
    public event Action? StateChanged;

    private readonly CfProxyBalancer _balancer = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Apply persisted port/secret before starting. A blank/invalid secret keeps the
    /// current random one; supply the persisted value so the tg:// link stays stable across runs.</summary>
    public void Configure(int port, string? secretHex)
    {
        if (port is > 0 and <= 65535) Port = port;
        if (!string.IsNullOrWhiteSpace(secretHex) && secretHex.Length == 32)
        {
            try { _secret = Convert.FromHexString(secretHex); }
            catch { /* keep existing */ }
        }
    }

    public bool Start()
    {
        if (IsRunning) return true;
        StartError = null;

        // Bind the configured port; if it's busy, fall back to the next few ports so a stale/other
        // listener on 1443 doesn't leave the user stuck. The bound port drives Host:Port and the link.
        TcpListener? listener = null;
        int desired = Port;
        for (int p = desired; p <= Math.Min(desired + 9, 65535); p++)
        {
            try { listener = new TcpListener(IPAddress.Loopback, p); listener.Start(); Port = p; break; }
            catch (SocketException) { listener = null; }
        }
        if (listener is null)
        {
            StartError = $"Порт {desired} занят (проверил {desired}–{Math.Min(desired + 9, 65535)}). " +
                         "Закройте программу, занявшую порт, или укажите другой в настройках.";
            LogLine?.Invoke($"[tg-proxy] {StartError}");
            StateChanged?.Invoke(); // let the card show the failure instead of silently staying off
            return false;
        }
        if (Port != desired)
            LogLine?.Invoke($"[tg-proxy] порт {desired} занят — использую {Port}");

        _listener = listener;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        LogLine?.Invoke($"[tg-proxy] запущен на 127.0.0.1:{Port} (секрет dd{SecretHex})");
        StateChanged?.Invoke();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        return true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        LogLine?.Invoke("[tg-proxy] остановлен");
        StateChanged?.Invoke();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        CryptoCtx? ctx = null;
        TgWebSocket? ws = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();

            byte[] handshake = await ReadExactAsync(stream, TgProxyProto.HandshakeLen, ct);
            var decoded = TgProxyProto.TryHandshake(handshake, _secret);
            if (decoded is null)
                return; // wrong secret / not an MTProto client

            var (dc, isMedia, protoTag, prekeyIv) = decoded.Value;
            int dcIdx = isMedia ? -dc : dc;
            uint protoInt = TgProxyProto.ProtoInt(protoTag);

            byte[] relayInit = TgProxyProto.GenerateRelayInit(protoTag, dcIdx);
            ctx = TgProxyProto.BuildCryptoCtx(prekeyIv, _secret, relayInit);

            ws = await ConnectUpstreamAsync(dc, isMedia, ct);
            if (ws is null)
            {
                LogLine?.Invoke($"[tg-proxy] DC{dc}: не удалось подключиться к Telegram");
                return;
            }

            var splitter = new MsgSplitter(relayInit, protoInt);
            await ws.SendAsync(relayInit, ct);
            await BridgeAsync(stream, ws, ctx, splitter, ct);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (EndOfStreamException) { /* client disconnected */ }
        catch (IOException) { /* connection reset */ }
        catch (Exception ex) { LogLine?.Invoke($"[tg-proxy] ошибка соединения: {ex.Message}"); }
        finally
        {
            if (ws is not null) { try { await ws.CloseAsync(); } catch { /* ignore */ } }
            ctx?.Dispose();
            try { client.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Try the direct DC IP (when configured) first, then Cloudflare-fronted domains.</summary>
    private async Task<TgWebSocket?> ConnectUpstreamAsync(int dc, bool isMedia, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(8);
        int attempts = 0;

        if (TgProxyProto.DcRedirects.TryGetValue(dc, out string? ip))
        {
            foreach (string domain in TgProxyProto.WsDomains(dc, isMedia))
            {
                if (attempts++ >= MaxUpstreamAttempts) break;
                var ws = await TgWebSocket.ConnectAsync(ip, domain, timeout, sni: domain, ct: ct);
                if (ws is not null) return ws;
            }
        }

        foreach (string baseDomain in _balancer.DomainsForDc(dc))
        {
            if (attempts++ >= MaxUpstreamAttempts) break;
            string domain = $"kws{dc}.{baseDomain}";
            var ws = await TgWebSocket.ConnectAsync(domain, domain, timeout, sni: domain, ct: ct);
            if (ws is not null) return ws;
        }
        return null;
    }

    /// <summary>Bidirectional relay with re-encryption: client bytes are decrypted with the client
    /// cipher, re-encrypted with the Telegram cipher and split into per-packet WS frames; Telegram
    /// frames make the reverse trip.</summary>
    private static async Task BridgeAsync(NetworkStream client, TgWebSocket ws, CryptoCtx ctx,
        MsgSplitter splitter, CancellationToken outerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        CancellationToken ct = linked.Token;
        long lastActivity = Environment.TickCount64;

        async Task ClientToTg()
        {
            var buf = new byte[65536];
            try
            {
                while (true)
                {
                    int r = await client.ReadAsync(buf, ct);
                    Volatile.Write(ref lastActivity, Environment.TickCount64);
                    if (r == 0)
                    {
                        var tail = splitter.Flush();
                        if (tail.Count > 0) await ws.SendAsync(tail[0], ct);
                        break;
                    }
                    byte[] plain = ctx.CltDec.Update(buf[..r]);
                    byte[] enc = ctx.TgEnc.Update(plain);
                    var parts = splitter.Split(enc);
                    if (parts.Count == 0) continue;
                    if (parts.Count > 1) await ws.SendBatchAsync(parts, ct);
                    else await ws.SendAsync(parts[0], ct);
                }
            }
            catch { /* falls through to cancel the peer loop */ }
            finally { linked.Cancel(); }
        }

        async Task TgToClient()
        {
            try
            {
                while (true)
                {
                    byte[]? data = await ws.RecvAsync(ct);
                    if (data is null) break;
                    Volatile.Write(ref lastActivity, Environment.TickCount64);
                    byte[] plain = ctx.TgDec.Update(data);
                    byte[] enc = ctx.CltEnc.Update(plain);
                    await client.WriteAsync(enc, ct);
                    await client.FlushAsync(ct);
                }
            }
            catch { /* falls through to cancel the peer loop */ }
            finally { linked.Cancel(); }
        }

        // Keepalive/idle watchdog: pings upstream so dead peers surface, and cancels the bridge once
        // it has been silent past the idle backstop — freeing the sockets/ciphers a stuck half-open
        // connection would otherwise pin forever.
        async Task Keepalive()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(KeepaliveMs, ct);
                    if (Environment.TickCount64 - Volatile.Read(ref lastActivity) > IdleTimeoutMs) break;
                    await ws.PingAsync(ct);
                }
            }
            catch { /* cancelled or the ping write failed on a dead socket */ }
            finally { linked.Cancel(); }
        }

        await Task.WhenAll(ClientToTg(), TgToClient(), Keepalive());
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await stream.ReadAsync(buf.AsMemory(off, n - off), ct);
            if (r == 0) throw new EndOfStreamException();
            off += r;
        }
        return buf;
    }

    public void Dispose() => Stop();
}
