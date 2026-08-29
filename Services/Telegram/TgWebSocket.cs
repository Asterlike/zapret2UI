using System.Buffers.Binary;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Zapret2UI.Services.Telegram;

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
    private const byte OpCont = 0x0;
    private const byte OpText = 0x1;
    private const byte OpBinary = 0x2;
    private const byte OpClose = 0x8;
    private const byte OpPing = 0x9;
    private const byte OpPong = 0xA;

    // Reject absurd frame lengths from a garbled/hostile edge (cert validation is skipped) so a bad
    // 64-bit length becomes a clean connection drop instead of an OverflowException / huge allocation.
    private const int MaxFrameLen = 16 * 1024 * 1024;

    // Typed as Stream, not SslStream: nothing past the handshake needs the TLS-specific surface, and it
    // lets the frame reader be driven from a plain in-memory stream in the tests.
    private readonly TcpClient? _tcp;
    private readonly Stream _ssl;
    // Serialises all writes to _ssl: data frames (SendAsync), pong/close replies (RecvAsync) and the
    // keepalive ping run on different tasks, and SslStream forbids concurrent writes.
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _closed;

    private long _bytesIn;
    private long _bytesOut;
    private int _assembled;

    /// <summary>Application-payload bytes read from the upstream (WS control frames excluded).</summary>
    public long BytesIn => Volatile.Read(ref _bytesIn);

    /// <summary>Application-payload bytes written to the upstream.</summary>
    public long BytesOut => Volatile.Read(ref _bytesOut);

    /// <summary>How many messages arrived split across several frames and had to be reassembled. Nonzero
    /// means the edge fragments — the case that silently truncated the stream before <see cref="RecvAsync"/>
    /// learned to join continuations.</summary>
    public int AssembledMessages => Volatile.Read(ref _assembled);

    private TgWebSocket(TcpClient? tcp, Stream ssl)
    {
        _tcp = tcp;
        _ssl = ssl;
    }

    /// <summary>Wrap an already-open stream, so the frame reader (fragment assembly in particular) can be
    /// exercised without a socket or a TLS handshake. Test-only.</summary>
    internal static TgWebSocket OverStream(Stream stream) => new(null, stream);

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
        Interlocked.Add(ref _bytesOut, data.Length);
    }

    public async Task SendBatchAsync(IReadOnlyList<byte[]> parts, CancellationToken ct)
    {
        if (_closed) throw new IOException("WebSocket closed");
        await _writeLock.WaitAsync(ct);
        try
        {
            foreach (var p in parts)
            {
                await _ssl.WriteAsync(BuildFrame(OpBinary, p), ct);
                Interlocked.Add(ref _bytesOut, p.Length);
            }
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
    /// returning null once the peer closes.
    ///
    /// A message may arrive SPLIT across frames (RFC 6455 fragmentation: a data frame with FIN=0, then
    /// continuation frames with opcode 0). Only large messages ever get fragmented, so this path is
    /// invisible to chat and hits file transfers — and dropping the continuations, as this used to,
    /// desynchronises the AES-CTR stream permanently: the client decrypts garbage from that point on and
    /// tears the connection down. Fragments are therefore joined here, and control frames (ping/pong)
    /// are answered mid-message without disturbing the assembly, exactly as the RFC allows.</summary>
    public async Task<byte[]?> RecvAsync(CancellationToken ct)
    {
        List<byte>? partial = null; // non-null while a fragmented message is being assembled
        while (!_closed)
        {
            (byte opcode, bool fin, byte[] payload) = await ReadFrameAsync(ct);
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
                case OpCont:
                    // A continuation with nothing open is a protocol violation; feeding its payload
                    // through would corrupt the stream, so drop the frame rather than the connection.
                    if (partial is null) continue;
                    partial.AddRange(payload);
                    if (partial.Count > MaxFrameLen)
                        throw new IOException($"WS message too large ({partial.Count})");
                    if (!fin) continue;
                    Interlocked.Increment(ref _assembled);
                    return partial.ToArray();
                case OpText:
                case OpBinary:
                    // FIN on the first frame = an ordinary whole message (the overwhelmingly common
                    // case, kept allocation-free). Otherwise this opens an assembly.
                    if (fin) return payload;
                    partial = new List<byte>(payload);
                    continue;
                default:
                    continue;
            }
        }
        return null;
    }

    private async Task<(byte opcode, bool fin, byte[] payload)> ReadFrameAsync(CancellationToken ct)
    {
        byte[] hdr = await ReadExactAsync(2, ct);
        bool fin = (hdr[0] & 0x80) != 0;
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
        if (opcode is OpCont or OpText or OpBinary) Interlocked.Add(ref _bytesIn, payload.Length);
        return (opcode, fin, payload);
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
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        try { _writeLock.Dispose(); } catch { /* ignore */ }
    }
}
