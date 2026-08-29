namespace Zapret2UI.Services.Telegram;

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
