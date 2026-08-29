using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Zapret2UI.Services.Telegram;

// Native C# port of the core wire protocol of Flowseal/tg-ws-proxy (MIT). It bridges a local
// MTProto proxy (what Telegram Desktop connects to on 127.0.0.1) to Telegram's data centers over
// WebSocket-TLS, using Cloudflare-fronted domains so the connection survives IP-based blocking.
//
// Only the "dd" (plain obfuscated MTProto) transport is implemented — the client link is local
// loopback, so FakeTLS/"ee" masking (only useful for a remote proxy) is intentionally omitted, as
// are the reference's pools / CF-worker / fronting / cooldown optimisations. What remains is the
// faithful happy path: obfuscated-handshake decode → relay re-obfuscation → re-encrypting bridge.

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

    /// <summary>Map the DC index a client put in its init onto a real Telegram datacentre (1..5).
    ///
    /// The protocol does not constrain this field, and clients are seen sending 203 (an alias for DC2).
    /// Only the relay init we forward upstream should carry the client's raw value; EVERYTHING else —
    /// the direct-IP table, the front blacklist, the WS hostnames and the log — must key off the real
    /// DC. Previously the 203 fix-up lived inside <see cref="WsDomains"/> alone, so such a connection
    /// silently skipped DC2's direct-IP fast path, got its own blacklist bucket, and printed as the
    /// nonsensical "DC203". Anything outside 1..5 falls back to DC2 (the one with a direct IP).</summary>
    public static int NormalizeDc(int dc)
    {
        if (dc == 203) return 2;
        return dc is >= 1 and <= 5 ? dc : 2;
    }

    /// <summary>Offset Telegram Desktop adds to a DC index to mark the TEST network (10001-10003).</summary>
    public const int TestDcOffset = 10_000;

    /// <summary>True when the client asked for Telegram's TEST infrastructure. We only ever reach the
    /// production edge, so such a connection cannot possibly authenticate — and without this check it
    /// would fall through <see cref="NormalizeDc"/> into production DC2 and hang as an eternal
    /// "подключение" with nothing in the journal to explain why.</summary>
    public static bool IsTestDc(int dc) => dc - TestDcOffset is >= 1 and <= 3;

    /// <summary>Base domain of the DIRECT upstream's TLS SNI (<c>kws{N}.web.telegram.org</c>). This is a
    /// real Telegram name on real Telegram addresses — the one path the engine can actually desync, and
    /// the one most connections take, since the direct IP is preferred for DC2/DC4. Seeded into the
    /// proxy's hostlist alongside the Cloudflare fronts so the engine covers both upstreams.</summary>
    public const string DirectWsDomain = "web.telegram.org";

    /// <summary>Upstream WebSocket SNI/Host candidates for a DC (kws-N being the "media" edge).</summary>
    public static string[] WsDomains(int dc, bool isMedia)
    {
        dc = NormalizeDc(dc);
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
