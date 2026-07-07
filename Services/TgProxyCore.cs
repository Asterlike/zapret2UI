using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ZapretUI.Services;

// Native C# port of the core wire protocol of Flowseal/tg-ws-proxy (MIT). It bridges a local
// MTProto proxy (what Telegram Desktop connects to on 127.0.0.1) to Telegram's data centers over
// WebSocket-TLS, using Cloudflare-fronted domains so the connection survives IP-based blocking.
//
// Only the "dd" (plain obfuscated MTProto) transport is implemented — the client link is local
// loopback, so FakeTLS/"ee" masking (only useful for a remote proxy) is intentionally omitted, as
// are the reference's pools / CF-worker / fronting / cooldown optimisations. What remains is the
// faithful happy path: obfuscated-handshake decode → relay re-obfuscation → re-encrypting bridge.

/// <summary>Stateful AES-256-CTR stream cipher (CTR is symmetric, so this serves both directions).
/// .NET ships no streaming CTR mode, so we drive AES-ECB over a big-endian 128-bit counter and XOR
/// the keystream, buffering the leftover keystream between calls.</summary>
internal sealed class AesCtr : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _keystream = new byte[16];
    private int _ksPos = 16; // 16 == no buffered keystream

    public AesCtr(byte[] key, byte[] iv)
    {
        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _aes.Key = key;
        _ecb = _aes.CreateEncryptor();
        Buffer.BlockCopy(iv, 0, _counter, 0, 16);
    }

    public byte[] Update(byte[] data)
    {
        var outp = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            if (_ksPos == 16)
            {
                _ecb.TransformBlock(_counter, 0, 16, _keystream, 0);
                IncrementBigEndian(_counter);
                _ksPos = 0;
            }
            outp[i] = (byte)(data[i] ^ _keystream[_ksPos++]);
        }
        return outp;
    }

    private static void IncrementBigEndian(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
            if (++counter[i] != 0) break;
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }
}

/// <summary>The four AES-CTR streams for a bridged connection: client-side decrypt/encrypt and
/// Telegram-side encrypt/decrypt. Data is re-encrypted as it crosses (client cipher ↔ Telegram
/// cipher) because the proxy re-obfuscates the stream with a fresh handshake toward Telegram.</summary>
internal sealed class CryptoCtx : IDisposable
{
    public readonly AesCtr CltDec; // decrypt data coming from the client
    public readonly AesCtr CltEnc; // encrypt data going to the client
    public readonly AesCtr TgEnc;  // encrypt data going to Telegram
    public readonly AesCtr TgDec;  // decrypt data coming from Telegram

    public CryptoCtx(AesCtr cltDec, AesCtr cltEnc, AesCtr tgEnc, AesCtr tgDec)
    {
        CltDec = cltDec; CltEnc = cltEnc; TgEnc = tgEnc; TgDec = tgDec;
    }

    public void Dispose()
    {
        CltDec.Dispose(); CltEnc.Dispose(); TgEnc.Dispose(); TgDec.Dispose();
    }
}

internal static class TgProxyProto
{
    public const int HandshakeLen = 64;
    public const int SkipLen = 8;
    public const int PrekeyLen = 32;
    public const int KeyLen = 32;
    public const int IvLen = 16;
    public const int ProtoTagPos = 56;
    public const int DcIdxPos = 60;

    public static readonly byte[] ProtoTagAbridged = { 0xef, 0xef, 0xef, 0xef };
    public static readonly byte[] ProtoTagIntermediate = { 0xee, 0xee, 0xee, 0xee };
    public static readonly byte[] ProtoTagSecure = { 0xdd, 0xdd, 0xdd, 0xdd };

    public const uint ProtoAbridgedInt = 0xEFEFEFEF;
    public const uint ProtoIntermediateInt = 0xEEEEEEEE;
    public const uint ProtoPaddedIntermediateInt = 0xDDDDDDDD;

    // DCs with a direct-IP WS target the reference tries before the Cloudflare fallback.
    public static readonly IReadOnlyDictionary<int, string> DcRedirects = new Dictionary<int, string>
    {
        [2] = "149.154.167.220",
        [4] = "149.154.167.220",
    };

    private static readonly byte[][] ReservedStarts =
    {
        new byte[] { 0x48, 0x45, 0x41, 0x44 }, // HEAD
        new byte[] { 0x50, 0x4F, 0x53, 0x54 }, // POST
        new byte[] { 0x47, 0x45, 0x54, 0x20 }, // "GET "
        new byte[] { 0xee, 0xee, 0xee, 0xee },
        new byte[] { 0xdd, 0xdd, 0xdd, 0xdd },
        new byte[] { 0x16, 0x03, 0x01, 0x02 },
    };

    /// <summary>Upstream WebSocket SNI/Host candidates for a DC (kws-N being the "media" edge).</summary>
    public static string[] WsDomains(int dc, bool isMedia)
    {
        if (dc == 203) dc = 2;
        return isMedia
            ? new[] { $"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org" }
            : new[] { $"kws{dc}.web.telegram.org", $"kws{dc}-1.web.telegram.org" };
    }

    /// <summary>Decode the client's 64-byte obfuscated-2 init: verify the transport tag and read the
    /// (signed) DC index. Returns null when the secret/protocol don't match.</summary>
    public static (int dc, bool isMedia, byte[] protoTag, byte[] prekeyIv)? TryHandshake(byte[] handshake, byte[] secret)
    {
        byte[] prekeyIv = handshake[SkipLen..(SkipLen + PrekeyLen + IvLen)]; // [8:56]
        byte[] prekey = prekeyIv[..PrekeyLen];
        byte[] iv = prekeyIv[PrekeyLen..];
        byte[] key = Sha256(prekey, secret);

        byte[] decrypted;
        using (var dec = new AesCtr(key, iv))
            decrypted = dec.Update(handshake);

        byte[] protoTag = decrypted[ProtoTagPos..(ProtoTagPos + 4)];
        if (!Eq(protoTag, ProtoTagAbridged) && !Eq(protoTag, ProtoTagIntermediate) && !Eq(protoTag, ProtoTagSecure))
            return null;

        short dcIdx = BinaryPrimitives.ReadInt16LittleEndian(decrypted.AsSpan(DcIdxPos, 2));
        return (Math.Abs(dcIdx), dcIdx < 0, protoTag, prekeyIv);
    }

    /// <summary>Build a fresh 64-byte obfuscated init to send to Telegram, encoding the transport tag
    /// and DC index in the last 8 bytes exactly as the reference does.</summary>
    public static byte[] GenerateRelayInit(byte[] protoTag, int dcIdx)
    {
        byte[] rnd;
        while (true)
        {
            rnd = RandomBytes(HandshakeLen);
            if (rnd[0] == 0xef) continue;
            if (ReservedStarts.Any(r => Eq(rnd[..4], r))) continue;
            if (rnd[4] == 0 && rnd[5] == 0 && rnd[6] == 0 && rnd[7] == 0) continue;
            break;
        }

        byte[] encKey = rnd[SkipLen..(SkipLen + PrekeyLen)];
        byte[] encIv = rnd[(SkipLen + PrekeyLen)..(SkipLen + PrekeyLen + IvLen)];

        var tailPlain = new byte[8];
        Buffer.BlockCopy(protoTag, 0, tailPlain, 0, 4);
        BinaryPrimitives.WriteInt16LittleEndian(tailPlain.AsSpan(4, 2), (short)dcIdx);
        Buffer.BlockCopy(RandomBytes(2), 0, tailPlain, 6, 2);

        byte[] encryptedFull;
        using (var enc = new AesCtr(encKey, encIv))
            encryptedFull = enc.Update(rnd);

        byte[] result = (byte[])rnd.Clone();
        for (int i = 0; i < 8; i++)
        {
            byte ks = (byte)(encryptedFull[ProtoTagPos + i] ^ rnd[ProtoTagPos + i]);
            result[ProtoTagPos + i] = (byte)(tailPlain[i] ^ ks);
        }
        return result;
    }

    /// <summary>SHA-256(prekey ‖ secret) — the obfuscation key derivation for the client leg. Public so
    /// the loopback bridge self-test can build the client-side ciphers.</summary>
    public static byte[] DeriveKey(byte[] prekey, byte[] secret) => Sha256(prekey, secret);

    /// <summary>Build a 64-byte obfuscated-2 init as a real Telegram CLIENT would send to the local proxy
    /// (key = SHA-256(prekey ‖ secret), unlike the secret-less relay init). Used only by the bridge
    /// self-test to drive the real bridge end-to-end from a loopback client.</summary>
    public static byte[] GenerateClientInit(byte[] protoTag, int dcIdx, byte[] secret)
    {
        byte[] rnd;
        while (true)
        {
            rnd = RandomBytes(HandshakeLen);
            if (rnd[0] == 0xef) continue;
            if (ReservedStarts.Any(r => Eq(rnd[..4], r))) continue;
            if (rnd[4] == 0 && rnd[5] == 0 && rnd[6] == 0 && rnd[7] == 0) continue;
            break;
        }

        byte[] prekey = rnd[SkipLen..(SkipLen + PrekeyLen)];
        byte[] encKey = Sha256(prekey, secret); // client mixes the secret into the key
        byte[] encIv = rnd[(SkipLen + PrekeyLen)..(SkipLen + PrekeyLen + IvLen)];

        var tailPlain = new byte[8];
        Buffer.BlockCopy(protoTag, 0, tailPlain, 0, 4);
        BinaryPrimitives.WriteInt16LittleEndian(tailPlain.AsSpan(4, 2), (short)dcIdx);
        Buffer.BlockCopy(RandomBytes(2), 0, tailPlain, 6, 2);

        byte[] encryptedFull;
        using (var enc = new AesCtr(encKey, encIv))
            encryptedFull = enc.Update(rnd);

        byte[] result = (byte[])rnd.Clone();
        for (int i = 0; i < 8; i++)
        {
            byte ks = (byte)(encryptedFull[ProtoTagPos + i] ^ rnd[ProtoTagPos + i]);
            result[ProtoTagPos + i] = (byte)(tailPlain[i] ^ ks);
        }
        return result;
    }

    public static CryptoCtx BuildCryptoCtx(byte[] clientPrekeyIv, byte[] secret, byte[] relayInit)
    {
        byte[] cltDecPrekey = clientPrekeyIv[..PrekeyLen];
        byte[] cltDecIv = clientPrekeyIv[PrekeyLen..];
        byte[] cltDecKey = Sha256(cltDecPrekey, secret);

        byte[] cltEncPrekeyIv = Reversed(clientPrekeyIv);
        byte[] cltEncKey = Sha256(cltEncPrekeyIv[..PrekeyLen], secret);
        byte[] cltEncIv = cltEncPrekeyIv[PrekeyLen..];

        var cltDecryptor = new AesCtr(cltDecKey, cltDecIv);
        var cltEncryptor = new AesCtr(cltEncKey, cltEncIv);
        cltDecryptor.Update(new byte[64]); // fast-forward past the 64-byte init already consumed

        byte[] relayEncKey = relayInit[SkipLen..(SkipLen + PrekeyLen)];
        byte[] relayEncIv = relayInit[(SkipLen + PrekeyLen)..(SkipLen + PrekeyLen + IvLen)];

        byte[] relayDecPrekeyIv = Reversed(relayInit[SkipLen..(SkipLen + PrekeyLen + IvLen)]);
        byte[] relayDecKey = relayDecPrekeyIv[..KeyLen];
        byte[] relayDecIv = relayDecPrekeyIv[KeyLen..];

        var tgEncryptor = new AesCtr(relayEncKey, relayEncIv);
        var tgDecryptor = new AesCtr(relayDecKey, relayDecIv);
        tgEncryptor.Update(new byte[64]);

        return new CryptoCtx(cltDecryptor, cltEncryptor, tgEncryptor, tgDecryptor);
    }

    /// <summary>Minimal MTProto client probe over an already-upgraded WS: send the obfuscated relay init
    /// and one unauthenticated req_pq_multi, then wait for Telegram's resPQ frame. A front that only
    /// completes the 101 upgrade but doesn't actually bridge to a live DC (→ Telegram's eternal
    /// "подключение") never answers — so this separates a working path from a dead one, which a bare
    /// handshake check can't. Returns true iff any bytes come back within the timeout.</summary>
    public static async Task<bool> ProbeRelayAsync(TgWebSocket ws, int dc, CancellationToken ct, byte[]? protoTag = null)
    {
        protoTag ??= ProtoTagIntermediate; // 4-byte-LE framing (also a valid padded/secure packet, 0 padding)
        byte[] relayInit = GenerateRelayInit(protoTag, dc);

        using var enc = new AesCtr(relayInit[SkipLen..(SkipLen + PrekeyLen)],
                                   relayInit[(SkipLen + PrekeyLen)..(SkipLen + PrekeyLen + IvLen)]);
        enc.Update(new byte[64]); // fast-forward past the init, like the relay's Telegram-side encryptor

        // Unauthenticated req_pq_multi: auth_key_id(0) | msg_id | len(20) | (ctor 0xbe7e8ef1 | nonce16).
        var msg = new byte[8 + 8 + 4 + 20];
        long msgId = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32) & ~3L;
        BinaryPrimitives.WriteInt64LittleEndian(msg.AsSpan(8), msgId);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(16), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(20), 0xbe7e8ef1);
        RandomBytes(16).CopyTo(msg, 24);

        // Intermediate transport frame ([len LE] | msg), obfuscated with the relay stream cipher.
        var framed = new byte[4 + msg.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, msg.Length);
        msg.CopyTo(framed, 4);
        byte[] encFramed = enc.Update(framed);

        await ws.SendAsync(relayInit, ct);
        await ws.SendAsync(encFramed, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(5000);
        try
        {
            byte[]? resp = await ws.RecvAsync(timeout.Token);
            return resp is { Length: > 0 }; // any bytes back = Telegram answered through this front
        }
        catch { return false; }
    }

    public static uint ProtoInt(byte[] protoTag)
    {
        if (Eq(protoTag, ProtoTagAbridged)) return ProtoAbridgedInt;
        if (Eq(protoTag, ProtoTagIntermediate)) return ProtoIntermediateInt;
        return ProtoPaddedIntermediateInt;
    }

    public static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    private static byte[] Sha256(byte[] a, byte[] b)
    {
        var buf = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, buf, 0, a.Length);
        Buffer.BlockCopy(b, 0, buf, a.Length, b.Length);
        return SHA256.HashData(buf);
    }

    private static byte[] Reversed(byte[] a)
    {
        var r = (byte[])a.Clone();
        Array.Reverse(r);
        return r;
    }

    private static bool Eq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}

/// <summary>Splits the re-encrypted client→Telegram stream into individual MTProto transport packets
/// so each is delivered as its own WebSocket frame (what the /apiws endpoint expects). It keeps a
/// parallel decryptor seeded like the Telegram encryptor to read plaintext packet lengths while
/// forwarding the ciphertext untouched.</summary>
internal sealed class MsgSplitter
{
    private readonly AesCtr _dec;
    private readonly uint _proto;
    private readonly List<byte> _cipherBuf = new();
    private readonly List<byte> _plainBuf = new();
    private bool _disabled;

    public MsgSplitter(byte[] relayInit, uint protoInt)
    {
        _dec = new AesCtr(relayInit[TgProxyProto.SkipLen..(TgProxyProto.SkipLen + TgProxyProto.PrekeyLen)],
                          relayInit[(TgProxyProto.SkipLen + TgProxyProto.PrekeyLen)..(TgProxyProto.SkipLen + TgProxyProto.PrekeyLen + TgProxyProto.IvLen)]);
        _dec.Update(new byte[64]);
        _proto = protoInt;
    }

    public List<byte[]> Split(byte[] chunk)
    {
        var parts = new List<byte[]>();
        if (chunk.Length == 0) return parts;
        if (_disabled) { parts.Add(chunk); return parts; }

        _cipherBuf.AddRange(chunk);
        _plainBuf.AddRange(_dec.Update(chunk));

        int offset = 0;
        int bufLen = _cipherBuf.Count;
        while (offset < bufLen)
        {
            int? packetLen = NextPacketLen(offset, bufLen - offset);
            if (packetLen is null) break;
            if (packetLen <= 0)
            {
                parts.Add(_cipherBuf.GetRange(offset, bufLen - offset).ToArray());
                offset = bufLen;
                _disabled = true;
                break;
            }
            parts.Add(_cipherBuf.GetRange(offset, packetLen.Value).ToArray());
            offset += packetLen.Value;
        }

        if (offset > 0)
        {
            _cipherBuf.RemoveRange(0, offset);
            _plainBuf.RemoveRange(0, offset);
        }
        return parts;
    }

    public List<byte[]> Flush()
    {
        var parts = new List<byte[]>();
        if (_cipherBuf.Count == 0) return parts;
        parts.Add(_cipherBuf.ToArray());
        _cipherBuf.Clear();
        _plainBuf.Clear();
        return parts;
    }

    private int? NextPacketLen(int offset, int avail)
    {
        if (avail <= 0) return null;
        if (_proto == TgProxyProto.ProtoAbridgedInt) return NextAbridgedLen(offset, avail);
        if (_proto is TgProxyProto.ProtoIntermediateInt or TgProxyProto.ProtoPaddedIntermediateInt)
            return NextIntermediateLen(offset, avail);
        return 0;
    }

    private int? NextAbridgedLen(int offset, int avail)
    {
        byte first = _plainBuf[offset];
        int payloadLen, headerLen;
        if (first is 0x7F or 0xFF)
        {
            if (avail < 4) return null;
            payloadLen = (_plainBuf[offset + 1] | (_plainBuf[offset + 2] << 8) | (_plainBuf[offset + 3] << 16)) * 4;
            headerLen = 4;
        }
        else
        {
            payloadLen = (first & 0x7F) * 4;
            headerLen = 1;
        }
        if (payloadLen <= 0) return 0;
        int packetLen = headerLen + payloadLen;
        return avail < packetLen ? null : packetLen;
    }

    private int? NextIntermediateLen(int offset, int avail)
    {
        if (avail < 4) return null;
        uint payloadLen = ((uint)(_plainBuf[offset] | (_plainBuf[offset + 1] << 8) | (_plainBuf[offset + 2] << 16) | (_plainBuf[offset + 3] << 24))) & 0x7FFFFFFFu;
        if (payloadLen == 0) return 0;
        long packetLen = 4 + payloadLen;
        return avail < packetLen ? null : (int)packetLen;
    }
}

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

    private readonly string[] _domains;
    private readonly Dictionary<int, string> _dcToDomain = new();
    private readonly ConcurrentDictionary<string, long> _bad = new(); // "dc|domain" → expiry (TickCount64)
    private readonly Random _rng = new();

    public CfProxyBalancer()
    {
        _domains = Encoded.Select(Decode).ToArray();
        foreach (int dc in new[] { 1, 2, 3, 4, 5, 203 })
            _dcToDomain[dc] = _domains[_rng.Next(_domains.Length)];
    }

    /// <summary>Yield the per-DC sticky domain first, then the rest shuffled — skipping any front
    /// recently seen not to relay (see <see cref="MarkBad"/>), so a client's retry rotates off it.</summary>
    public IEnumerable<string> DomainsForDc(int dc)
    {
        _dcToDomain.TryGetValue(dc, out string? current);
        if (current is not null && IsBad(dc, current)) current = null; // rotate off a dead sticky
        if (current is not null) yield return current;
        foreach (string d in _domains.OrderBy(_ => _rng.Next()))
            if (d != current && !IsBad(dc, d)) yield return d;
    }

    /// <summary>Cool a front down for this DC (skip it in <see cref="DomainsForDc"/> for
    /// <paramref name="cooldownMs"/>, then it heals). Default ~2 min for a non-relaying front; callers
    /// pass shorter windows for softer reasons (e.g. a CF 429 or an instantly-dropped connection).</summary>
    public void MarkBad(int dc, string baseDomain, int cooldownMs = 120_000) =>
        _bad[$"{dc}|{baseDomain}"] = Environment.TickCount64 + cooldownMs;

    private bool IsBad(int dc, string baseDomain) =>
        _bad.TryGetValue($"{dc}|{baseDomain}", out long exp) && exp > Environment.TickCount64;

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

/// <summary>Which phase of the upstream WebSocket connect failed, so the proxy can log a human cause
/// (TCP vs TLS vs WS-upgrade) instead of a bare "не удалось".</summary>
internal enum WsStage { Tcp, Tls, Upgrade, Ok }

/// <summary>Outcome of <see cref="TgWebSocket.ConnectAsync"/>: the socket on success, else the phase
/// that failed and (for an upgrade failure) the HTTP status that came back instead of 101.</summary>
internal readonly record struct WsResult(TgWebSocket? Ws, WsStage Stage, int Status)
{
    public bool Ok => Ws is not null;
}

/// <summary>Minimal client WebSocket over a raw <see cref="SslStream"/>: connects to an IP with an
/// independent SNI/Host, skips certificate validation (the transport carries its own MTProto crypto),
/// and speaks client-masked binary frames — mirroring tg-ws-proxy's raw_websocket.</summary>
internal sealed class TgWebSocket : IDisposable
{
    private const byte OpBinary = 0x2;
    private const byte OpClose = 0x8;
    private const byte OpPing = 0x9;
    private const byte OpPong = 0xA;

    // Reject absurd frame lengths from a garbled/hostile edge (cert validation is skipped) so a bad
    // 64-bit length becomes a clean connection drop instead of an OverflowException / huge allocation.
    private const int MaxFrameLen = 16 * 1024 * 1024;

    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    // Serialises all writes to _ssl: data frames (SendAsync), pong/close replies (RecvAsync) and the
    // keepalive ping run on different tasks, and SslStream forbids concurrent writes.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _closed;

    private TgWebSocket(TcpClient tcp, SslStream ssl)
    {
        _tcp = tcp;
        _ssl = ssl;
    }

    public static async Task<WsResult> ConnectAsync(string host, string domain, TimeSpan timeout,
        string? sni = null, string path = "/apiws", CancellationToken ct = default)
    {
        sni ??= domain;
        var tcp = new TcpClient();
        SslStream? ssl = null;
        var stage = WsStage.Tcp; // advances as each phase is entered, so the catch reports where it broke
        try
        {
            await tcp.ConnectAsync(host, 443, ct).AsTask().WaitAsync(timeout, ct);
            tcp.NoDelay = true;

            stage = WsStage.Tls;
            ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = sni }, ct)
                     .WaitAsync(timeout, ct);

            stage = WsStage.Upgrade;
            string wsKey = Convert.ToBase64String(TgProxyProto.RandomBytes(16));
            string req =
                $"GET {path} HTTP/1.1\r\n" +
                $"Host: {domain}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {wsKey}\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "Sec-WebSocket-Protocol: binary\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(req), ct);
            await ssl.FlushAsync(ct);

            int status = await ReadStatusAsync(ssl, timeout, ct);
            if (status == 101)
                return new WsResult(new TgWebSocket(tcp, ssl), WsStage.Ok, status);

            ssl.Dispose();
            tcp.Dispose();
            return new WsResult(null, WsStage.Upgrade, status);
        }
        catch
        {
            try { ssl?.Dispose(); } catch { /* ignore */ }
            tcp.Dispose();
            return new WsResult(null, stage, 0);
        }
    }

    private static async Task<int> ReadStatusAsync(SslStream ssl, TimeSpan timeout, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        int status = 0;
        bool firstLineDone = false;
        while (true)
        {
            int n = await ssl.ReadAsync(one, ct).AsTask().WaitAsync(timeout, ct);
            // Premature EOF before the \r\n\r\n header terminator = an incomplete handshake response.
            // Returning the parsed status here would accept a truncated "HTTP/1.1 101 …" as a successful
            // upgrade; fail instead (the success path returns only at the terminator below).
            if (n == 0) return 0;
            sb.Append((char)one[0]);
            int len = sb.Length;
            if (len > 16384) return 0; // malformed / oversized response headers → fail (defensive cap)
            if (!firstLineDone && len >= 2 && sb[len - 2] == '\r' && sb[len - 1] == '\n')
            {
                string first = sb.ToString(0, len - 2);
                string[] parts = first.Split(' ');
                if (parts.Length >= 2) int.TryParse(parts[1], out status);
                firstLineDone = true;
            }
            if (len >= 4 && sb[len - 4] == '\r' && sb[len - 3] == '\n' && sb[len - 2] == '\r' && sb[len - 1] == '\n')
                return status;
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        if (_closed) throw new IOException("WebSocket closed");
        await WriteLockedAsync(BuildFrame(OpBinary, data), ct);
    }

    public async Task SendBatchAsync(IReadOnlyList<byte[]> parts, CancellationToken ct)
    {
        if (_closed) throw new IOException("WebSocket closed");
        await _writeLock.WaitAsync(ct);
        try
        {
            foreach (var p in parts)
                await _ssl.WriteAsync(BuildFrame(OpBinary, p), ct);
            await _ssl.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Sends a WebSocket ping (keepalive): the write fails on a dead peer, which lets the
    /// bridge notice and tear the connection down.</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        if (_closed) throw new IOException("WebSocket closed");
        await WriteLockedAsync(BuildFrame(OpPing, Array.Empty<byte>()), ct);
    }

    private async Task WriteLockedAsync(byte[] frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _ssl.WriteAsync(frame, ct); await _ssl.FlushAsync(ct); }
        finally { _writeLock.Release(); }
    }

    /// <summary>Reads the next application (binary/text) message, transparently answering pings and
    /// returning null once the peer closes.</summary>
    public async Task<byte[]?> RecvAsync(CancellationToken ct)
    {
        while (!_closed)
        {
            (byte opcode, byte[] payload) = await ReadFrameAsync(ct);
            switch (opcode)
            {
                case OpClose:
                    _closed = true;
                    try { await WriteLockedAsync(BuildFrame(OpClose, Array.Empty<byte>()), ct); }
                    catch { /* peer already gone */ }
                    return null;
                case OpPing:
                    try { await WriteLockedAsync(BuildFrame(OpPong, payload), ct); }
                    catch { /* peer already gone */ }
                    continue;
                case OpPong:
                    continue;
                case 0x1:
                case 0x2:
                    return payload;
                default:
                    continue;
            }
        }
        return null;
    }

    private async Task<(byte opcode, byte[] payload)> ReadFrameAsync(CancellationToken ct)
    {
        byte[] hdr = await ReadExactAsync(2, ct);
        byte opcode = (byte)(hdr[0] & 0x0F);
        long length = hdr[1] & 0x7F;
        if (length == 126)
        {
            byte[] ext = await ReadExactAsync(2, ct);
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            byte[] ext = await ReadExactAsync(8, ct);
            length = BinaryPrimitives.ReadInt64BigEndian(ext);
        }
        if (length < 0 || length > MaxFrameLen)
            throw new IOException($"WS frame too large ({length})");

        byte[]? mask = null;
        if ((hdr[1] & 0x80) != 0)
            mask = await ReadExactAsync(4, ct);

        byte[] payload = await ReadExactAsync((int)length, ct);
        if (mask is not null)
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= mask[i & 3];
        return (opcode, payload);
    }

    private async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        int off = 0;
        while (off < n)
        {
            int r = await _ssl.ReadAsync(buf.AsMemory(off, n - off), ct);
            if (r == 0) throw new EndOfStreamException();
            off += r;
        }
        return buf;
    }

    private static byte[] BuildFrame(byte opcode, byte[] data)
    {
        // Client frames are always masked per RFC 6455.
        byte[] maskKey = TgProxyProto.RandomBytes(4);
        int len = data.Length;
        int headerLen = len < 126 ? 2 : len < 65536 ? 4 : 10;
        var frame = new byte[headerLen + 4 + len];
        frame[0] = (byte)(0x80 | opcode);
        int pos;
        if (len < 126)
        {
            frame[1] = (byte)(0x80 | len);
            pos = 2;
        }
        else if (len < 65536)
        {
            frame[1] = 0x80 | 126;
            frame[2] = (byte)(len >> 8);
            frame[3] = (byte)len;
            pos = 4;
        }
        else
        {
            frame[1] = 0x80 | 127;
            BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(2, 8), len);
            pos = 10;
        }
        Buffer.BlockCopy(maskKey, 0, frame, pos, 4);
        pos += 4;
        for (int i = 0; i < len; i++)
            frame[pos + i] = (byte)(data[i] ^ maskKey[i & 3]);
        return frame;
    }

    public async Task CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await WriteLockedAsync(BuildFrame(OpClose, Array.Empty<byte>()), CancellationToken.None); }
        catch { /* best effort */ }
        Dispose();
    }

    public void Dispose()
    {
        try { _ssl.Dispose(); } catch { /* ignore */ }
        try { _tcp.Dispose(); } catch { /* ignore */ }
        try { _writeLock.Dispose(); } catch { /* ignore */ }
    }
}
