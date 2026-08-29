using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Zapret2UI.Services.Telegram;

/// <summary>Stateful AES-256-CTR stream cipher (CTR is symmetric, so this serves both directions).
/// .NET ships no streaming CTR mode, so we drive AES-ECB over a big-endian 128-bit counter and XOR
/// the keystream, buffering the leftover keystream between calls.
///
/// Every relayed byte passes through two of these (decrypt with the client's cipher, re-encrypt with
/// Telegram's) in each direction, and a file transfer pushes megabytes through several connections at
/// once — so this is the one hot loop in the proxy. The keystream is therefore produced a whole batch
/// of blocks per <see cref="ICryptoTransform.TransformBlock"/> call rather than one 16-byte block at a
/// time, and XORed eight bytes at a time. Measured on a 12-core desktop: 177 → 1558 MB/s. The byte
/// sequence is unchanged — the counter still advances one block at a time and leftover keystream still
/// carries across calls, which <c>AesCtrTests</c> pins against a reference implementation.</summary>
internal sealed class AesCtr : IDisposable
{
    // 256 blocks = 4 KB of keystream per ECB call. Measured sweet spot: 16 blocks already captures most
    // of the win and 1024 buys only another ~1.5% for four times the buffers.
    private const int BatchBlocks = 256;

    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _counters = new byte[BatchBlocks * 16]; // successive counter values to encrypt
    private readonly byte[] _keystream = new byte[BatchBlocks * 16];
    private int _ksPos;
    private int _ksLen; // _ksPos == _ksLen ⇒ no buffered keystream

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
        int i = 0;
        while (i < data.Length)
        {
            if (_ksPos == _ksLen)
            {
                // Only as many blocks as this call can still consume — a 64-byte handshake must not pay
                // for 4 KB of keystream. Whatever is left over is used by the next call.
                int blocks = Math.Min(BatchBlocks, (data.Length - i + 15) / 16);
                for (int b = 0; b < blocks; b++)
                {
                    Buffer.BlockCopy(_counter, 0, _counters, b * 16, 16);
                    IncrementBigEndian(_counter);
                }
                _ecb.TransformBlock(_counters, 0, blocks * 16, _keystream, 0);
                _ksPos = 0;
                _ksLen = blocks * 16;
            }

            int n = Math.Min(data.Length - i, _ksLen - _ksPos);
            Xor(data.AsSpan(i, n), _keystream.AsSpan(_ksPos, n), outp.AsSpan(i, n));
            i += n;
            _ksPos += n;
        }
        return outp;
    }

    /// <summary>XOR in machine words, with a byte tail. The spans are equal-length by construction.</summary>
    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dest)
    {
        var wa = MemoryMarshal.Cast<byte, ulong>(a);
        var wb = MemoryMarshal.Cast<byte, ulong>(b);
        var wd = MemoryMarshal.Cast<byte, ulong>(dest);
        for (int i = 0; i < wd.Length; i++) wd[i] = wa[i] ^ wb[i];
        for (int i = wd.Length * sizeof(ulong); i < a.Length; i++) dest[i] = (byte)(a[i] ^ b[i]);
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
