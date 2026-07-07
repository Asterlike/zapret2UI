using System.Buffers.Binary;
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

    // If the client has sent its handshake but Telegram answers nothing within this window, the front
    // upgraded to WS 101 but doesn't actually relay — tear the bridge down so the caller can blacklist
    // the front and the client's retry rotates to another (instead of Telegram "подключение" forever).
    private const int FirstResponseMs = 10_000;

    // A front that RELAYED but whose connection the UPSTREAM drops faster than this (the client didn't
    // close it) is flaky on this network — rotate off it briefly so the retry lands elsewhere. A healthy
    // (e.g. home/desktop) connection lives far longer, so this never fires there → no harm to PC users.
    private const int FlakyDeathMs = 6_000;

    private enum BridgeOutcome { Ok, DeadFront, FlakyDeath }

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
    private readonly DohResolver _doh = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _loggedFirstSuccess; // log "соединение установлено" once per session, not per connection

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
        _loggedFirstSuccess = false;
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

            var up = await ConnectUpstreamAsync(dc, isMedia, ct);
            if (up is null)
                return; // ConnectUpstreamAsync already logged the specific cause (DNS/TCP/TLS/upgrade)
            ws = up.Value.Ws;

            var splitter = new MsgSplitter(relayInit, protoInt);
            await ws.SendAsync(relayInit, ct);
            var outcome = await BridgeAsync(stream, ws, ctx, splitter, dc, ct);
            if (up.Value.FrontId is { } frontId)
            {
                if (outcome == BridgeOutcome.DeadFront)
                {
                    _balancer.MarkBad(dc, frontId);
                    _loggedFirstSuccess = false; // let the next working path re-announce success
                    LogLine?.Invoke($"[tg-proxy] DC{dc}: {frontId} не доводит трафик до Telegram — исключаю на время");
                }
                else if (outcome == BridgeOutcome.FlakyDeath)
                {
                    // Relayed but the upstream killed it almost instantly (mobile TSPU/CF) — rotate off it
                    // briefly so the client's retry lands on a different front instead of churning here.
                    _balancer.MarkBad(dc, frontId, 30_000);
                    _loggedFirstSuccess = false;
                    LogLine?.Invoke($"[tg-proxy] DC{dc}: {frontId} рвёт соединение сразу — пробую другой фронт");
                }
            }
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

    /// <summary>Try the direct DC IP (when configured) first, then Cloudflare-fronted domains resolved
    /// via DoH (falling back to the OS resolver). Logs the first successful path once, and a
    /// human-readable cause on total failure so a blocked user sees whether it's DNS, IP or TLS.</summary>
    private async Task<(TgWebSocket Ws, string? FrontId)?> ConnectUpstreamAsync(int dc, bool isMedia, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(8);
        int attempts = 0;
        var fails = new List<string>();

        // 1) Direct DC IP (DC2/DC4): no DNS needed, but the SNI is the real telegram host → SNI-blockable.
        //    FrontId=null: the real Telegram edge either relays or is TCP-blocked, so never blacklist it.
        if (TgProxyProto.DcRedirects.TryGetValue(dc, out string? ip))
        {
            foreach (string domain in TgProxyProto.WsDomains(dc, isMedia))
            {
                if (attempts++ >= MaxUpstreamAttempts) break;
                var r = await TgWebSocket.ConnectAsync(ip, domain, timeout, sni: domain, ct: ct);
                if (r.Ok) return LogSuccess(dc, $"{ip} ({domain})", r.Ws!, null);
                fails.Add($"{domain}: {StageText(r)}");
            }
        }

        // 2) Cloudflare-fronted domains: resolve via DoH first (bypasses ISP DNS poisoning), OS DNS second.
        //    FrontId=baseDomain: if it upgrades but doesn't relay, the bridge watchdog blacklists it.
        foreach (string baseDomain in _balancer.DomainsForDc(dc))
        {
            if (attempts++ >= MaxUpstreamAttempts) break;
            string domain = $"kws{dc}.{baseDomain}";

            string host, via;
            string[] dohIps = await _doh.ResolveAsync(domain, ct);
            if (dohIps.Length > 0) { host = dohIps[0]; via = $"DoH {host}"; }
            else
            {
                string? osIp = await TryOsResolveAsync(domain, ct);
                if (osIp is null) { fails.Add($"{domain}: DNS не резолвится"); continue; }
                host = osIp; via = $"DNS {host}";
            }

            var r = await TgWebSocket.ConnectAsync(host, domain, timeout, sni: domain, ct: ct);
            if (r.Ok) return LogSuccess(dc, $"{domain} ({via})", r.Ws!, baseDomain);
            // CF rate-limit (429) — common on mobile CGNAT where many users hammer the same shared
            // domains: cool this one down and rotate, exactly like the reference does.
            if (r.Status == 429) _balancer.MarkBad(dc, baseDomain, 45_000);
            fails.Add($"{domain} ({via}): {StageText(r)}");
        }

        string summary = fails.Count == 0 ? "нет доступных адресов" : string.Join("; ", fails.Take(4));
        LogLine?.Invoke($"[tg-proxy] DC{dc}: не удалось подключиться к Telegram — {summary}");
        return null;
    }

    private (TgWebSocket Ws, string? FrontId) LogSuccess(int dc, string via, TgWebSocket ws, string? frontId)
    {
        if (!_loggedFirstSuccess)
        {
            _loggedFirstSuccess = true;
            // Only the WS channel is open here (101) — NOT proof Telegram's traffic flows. The bridge
            // logs "поток пошёл" on the first real answer, or the front is blacklisted as not-relaying.
            LogLine?.Invoke($"[tg-proxy] канал до Telegram открыт (DC{dc} через {via}) — проверяю поток");
        }
        return (ws, frontId);
    }

    private static string StageText(WsResult r) => r.Stage switch
    {
        WsStage.Tcp => "TCP не открылся (таймаут / блок IP)",
        WsStage.Tls => "TLS оборван (DPI по SNI?)",
        WsStage.Upgrade => $"WebSocket-ответ {r.Status}, не 101",
        _ => "ошибка",
    };

    private static async Task<string?> TryOsResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, ct);
            foreach (var a in addrs)
                if (a.AddressFamily == AddressFamily.InterNetwork) return a.ToString();
            return addrs.Length > 0 ? addrs[0].ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>Probe the upstream paths to Telegram (DoH, system DNS, direct IP, Cloudflare fronts) and
    /// report where it breaks — for a user whose proxy "pings but won't connect". Logs each step and
    /// returns a short verdict for the card. Needs no admin and is independent of the local listener.</summary>
    public async Task<string> SelfTestAsync(CancellationToken ct = default)
    {
        LogLine?.Invoke("[tg-proxy] ── проверка соединения с Telegram ──");
        var timeout = TimeSpan.FromSeconds(8);
        const int dc = 2;
        bool anyOk = false;
        string okVia = "";

        // DoH reachability + DNS-poisoning check on a representative front domain.
        string probe = $"kws{dc}.{_balancer.DomainsForDc(dc).First()}";
        string[] dohIps = await _doh.ResolveAsync(probe, ct);
        LogLine?.Invoke(dohIps.Length > 0
            ? $"[tg-proxy] DoH (1.1.1.1 / 8.8.8.8): доступен ({probe} → {dohIps[0]})"
            : "[tg-proxy] DoH (1.1.1.1 / 8.8.8.8): недоступен — сеть, похоже, режет и его");
        string? osIp = await TryOsResolveAsync(probe, ct);
        if (osIp is null && dohIps.Length > 0)
            LogLine?.Invoke("[tg-proxy] системный DNS: фронт-домен не резолвится — вероятно отравление DNS у провайдера (DoH это обходит)");
        else if (osIp is not null)
            LogLine?.Invoke($"[tg-proxy] системный DNS: работает ({probe} → {osIp})");

        // Direct DC IP (its SNI is the real telegram host — чаще всего режется по SNI). A WS 101 alone
        // isn't enough — the front must actually relay to a live DC, so probe real data flow.
        if (TgProxyProto.DcRedirects.TryGetValue(dc, out string? ip))
        {
            string d = TgProxyProto.WsDomains(dc, false)[0];
            var r = await TgWebSocket.ConnectAsync(ip, d, timeout, sni: d, ct: ct);
            if (!r.Ok)
                LogLine?.Invoke($"[tg-proxy] прямой IP {ip} (SNI {d}): {StageText(r)}");
            else
            {
                bool relays = await TgProxyProto.ProbeRelayAsync(r.Ws!, dc, ct);
                r.Ws!.Dispose();
                LogLine?.Invoke(relays
                    ? $"[tg-proxy] прямой IP {ip}: OK (Telegram отвечает)"
                    : $"[tg-proxy] прямой IP {ip}: WS есть, но Telegram молчит (путь не доводит до DC)");
                if (relays) { anyOk = true; okVia = $"прямой IP {ip}"; }
            }
        }

        // Cloudflare-fronted domains (first few), each resolved via DoH → OS DNS, connected, then a real
        // relay probe: only a front that Telegram actually answers through counts as working.
        int tried = 0;
        foreach (string baseDomain in _balancer.DomainsForDc(dc))
        {
            if (tried++ >= 3) break;
            string domain = $"kws{dc}.{baseDomain}";
            string[] ips = await _doh.ResolveAsync(domain, ct);
            string host = ips.Length > 0 ? ips[0] : (await TryOsResolveAsync(domain, ct) ?? domain);
            var r = await TgWebSocket.ConnectAsync(host, domain, timeout, sni: domain, ct: ct);
            if (!r.Ok) { LogLine?.Invoke($"[tg-proxy] фронт {domain}: {StageText(r)}"); continue; }
            bool relays = await TgProxyProto.ProbeRelayAsync(r.Ws!, dc, ct);
            r.Ws!.Dispose();
            LogLine?.Invoke(relays
                ? $"[tg-proxy] фронт {domain}: OK (Telegram отвечает)"
                : $"[tg-proxy] фронт {domain}: WS есть, но Telegram молчит (мёртвый фронт)");
            if (relays) { if (!anyOk) { anyOk = true; okVia = domain; } break; }
        }

        string verdict = anyOk
            ? $"РАБОТАЕТ: найден путь до Telegram ({okVia}). Если Telegram всё равно не грузит — переподключите прокси в приложении Telegram."
            : "НЕ РАБОТАЕТ: до Telegram не достучаться на этой сети. Подробности — в журнале (DNS → поможет DoH; TLS/IP → провайдер режет Cloudflare).";
        LogLine?.Invoke("[tg-proxy] итог: " + verdict);
        return verdict;
    }

    /// <summary>Bidirectional relay with re-encryption: client bytes are decrypted with the client
    /// cipher, re-encrypted with the Telegram cipher and split into per-packet WS frames; Telegram
    /// frames make the reverse trip.</summary>
    /// <summary>Drive the REAL bridge from a loopback client: connect to our own listener as a Telegram
    /// client (secure/dd, like the desktop app), send req_pq through the bridge and check that Telegram's
    /// resPQ survives the round-trip DECODABLE — i.e. the re-encryption + splitter are correct. This is
    /// the piece the front self-test skips, and it's testable non-elevated. Returns a short verdict.</summary>
    public async Task<string> BridgeSelfTestAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return "тест моста: прокси не запущен";
        LogLine?.Invoke("[tg-proxy] ── тест моста (loopback-клиент через реальный мост) ──");
        using var cli = new TcpClient { NoDelay = true };
        try
        {
            await cli.ConnectAsync(Host, Port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
            var ns = cli.GetStream();

            byte[] protoTag = TgProxyProto.ProtoTagSecure; // match the real "dd" desktop client
            byte[] init = TgProxyProto.GenerateClientInit(protoTag, 2, _secret);
            byte[] prekeyIv = init[8..56];
            byte[] rev = (byte[])prekeyIv.Clone();
            Array.Reverse(rev);
            using var send = new AesCtr(TgProxyProto.DeriveKey(prekeyIv[..32], _secret), prekeyIv[32..]);
            using var recv = new AesCtr(TgProxyProto.DeriveKey(rev[..32], _secret), rev[32..]);
            send.Update(new byte[64]); // fast-forward past the init (mirrors the proxy's CltDec)

            await ns.WriteAsync(init, ct);

            // req_pq_multi, intermediate/secure framing: [len:4 LE][auth_key_id:0][msg_id][len:20][ctor|nonce]
            var msg = new byte[8 + 8 + 4 + 20];
            long msgId = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32) & ~3L;
            BinaryPrimitives.WriteInt64LittleEndian(msg.AsSpan(8), msgId);
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(16), 20);
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(20), 0xbe7e8ef1);
            TgProxyProto.RandomBytes(16).CopyTo(msg, 24);
            var framed = new byte[4 + msg.Length];
            BinaryPrimitives.WriteInt32LittleEndian(framed, msg.Length);
            msg.CopyTo(framed, 4);
            await ns.WriteAsync(send.Update(framed), ct);
            await ns.FlushAsync(ct);

            // Read the response through the bridge, decrypt with the client cipher, look for resPQ's
            // constructor (0x05162463) at the fixed offset — proof the bytes came back intact.
            var buf = new byte[2048];
            var plain = new List<byte>();
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(9000);
            try
            {
                while (plain.Count < 28)
                {
                    int n = await ns.ReadAsync(buf, to.Token);
                    if (n <= 0) break;
                    plain.AddRange(recv.Update(buf.AsSpan(0, n).ToArray()));
                }
            }
            catch (OperationCanceledException) { /* timed out */ }

            string verdict;
            if (plain.Count < 28)
                verdict = $"МОСТ: resPQ не дошёл ({plain.Count}б) — мост не донёс ответ Telegram до клиента";
            else
            {
                uint ctor = BinaryPrimitives.ReadUInt32LittleEndian(plain.ToArray().AsSpan(24, 4));
                verdict = ctor == 0x05162463
                    ? "МОСТ OK: resPQ прошёл через мост неповреждённым — re-encryption и splitter исправны"
                    : $"МОСТ ПОВРЕЖДЁН: ждал resPQ 0x05162463, пришло 0x{ctor:x8} — баг в мосте (не сеть!)";
            }
            LogLine?.Invoke("[tg-proxy] " + verdict);
            return verdict;
        }
        catch (Exception ex) { return "тест моста: ошибка " + ex.Message; }
    }

    /// <returns>How the bridge ended: <see cref="BridgeOutcome.DeadFront"/> (relayed nothing),
    /// <see cref="BridgeOutcome.FlakyDeath"/> (relayed but the upstream dropped it almost instantly), or
    /// <see cref="BridgeOutcome.Ok"/> — the caller cools the front down accordingly.</returns>
    private async Task<BridgeOutcome> BridgeAsync(NetworkStream client, TgWebSocket ws, CryptoCtx ctx,
        MsgSplitter splitter, int dc, CancellationToken outerCt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        CancellationToken ct = linked.Token;
        long lastActivity = Environment.TickCount64;
        long clientSpokeAt = 0; // TickCount64 of the first client→Telegram bytes (0 = client silent so far)
        int tgResponded = 0;    // set to 1 once Telegram sends ≥1 frame back
        int clientClosed = 0;   // set to 1 when the CLIENT closes gracefully (r==0) — not an upstream drop
        bool deadFront = false; // client spoke but Telegram never answered → front upgraded but doesn't relay
        long startTick = Environment.TickCount64;

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
                        Volatile.Write(ref clientClosed, 1); // client hung up gracefully — not an upstream kill
                        var tail = splitter.Flush();
                        if (tail.Count > 0) await ws.SendAsync(tail[0], ct);
                        break;
                    }
                    if (Volatile.Read(ref clientSpokeAt) == 0) Volatile.Write(ref clientSpokeAt, Environment.TickCount64);
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
                    if (Interlocked.Exchange(ref tgResponded, 1) == 0)
                        LogLine?.Invoke($"[tg-proxy] DC{dc}: пошли данные — держу соединение");
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

        // Dead-front watch: a healthy front answers within one round-trip, so this never fires on a good
        // connection (tgResponded flips fast). It only catches a front that upgraded to WS 101 but never
        // carries Telegram's traffic — then tears the bridge down so the caller blacklists it.
        async Task FirstResponseWatch()
        {
            try
            {
                while (Volatile.Read(ref tgResponded) == 0)
                {
                    await Task.Delay(1500, ct);
                    if (Volatile.Read(ref tgResponded) != 0) return; // healthy → stop watching, keep the bridge UP
                    long spoke = Volatile.Read(ref clientSpokeAt);
                    if (spoke != 0 && Environment.TickCount64 - spoke > FirstResponseMs)
                    {
                        deadFront = true;
                        linked.Cancel(); // ONLY a genuinely dead front tears the bridge down
                        return;
                    }
                }
            }
            catch { /* cancelled elsewhere (connection closed) */ }
        }

        await Task.WhenAll(ClientToTg(), TgToClient(), Keepalive(), FirstResponseWatch());

        long lifeMs = Environment.TickCount64 - startTick;
        bool relayed = Volatile.Read(ref tgResponded) != 0;
        // Lifetime of a connection that actually flowed — the churn tell: sub-second repeatedly = still
        // torn down early; long/rare = healthy. (Silent/failed connections aren't logged to avoid spam.)
        if (relayed)
            LogLine?.Invoke($"[tg-proxy] DC{dc}: соединение закрыто через {lifeMs / 1000.0:0.0}с");

        if (deadFront) return BridgeOutcome.DeadFront;
        // Relayed, lived < FlakyDeathMs, and the CLIENT didn't close it → the upstream dropped it fast.
        if (relayed && Volatile.Read(ref clientClosed) == 0 && lifeMs < FlakyDeathMs) return BridgeOutcome.FlakyDeath;
        return BridgeOutcome.Ok;
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

    public void Dispose()
    {
        Stop();
        _doh.Dispose();
    }
}
