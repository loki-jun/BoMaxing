using System.Buffers.Binary;

namespace BoMaxing.Core.Communication;

public enum ProtocolChecksum
{
    None,
    Xor8,
    Crc16Modbus
}

public sealed class ProtocolFrameTemplate
{
    public ProtocolFrameTemplate(
        byte[] header,
        byte[] footer,
        ProtocolChecksum checksum = ProtocolChecksum.None,
        int minimumPayloadLength = 0,
        int maximumPayloadLength = 4096)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(footer);
        if (minimumPayloadLength < 0 || maximumPayloadLength < minimumPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));
        }

        Header = header.ToArray();
        Footer = footer.ToArray();
        Checksum = checksum;
        MinimumPayloadLength = minimumPayloadLength;
        MaximumPayloadLength = maximumPayloadLength;
    }

    public byte[] Header { get; }
    public byte[] Footer { get; }
    public ProtocolChecksum Checksum { get; }
    public int MinimumPayloadLength { get; }
    public int MaximumPayloadLength { get; }
}

public static class ProtocolFrameCodec
{
    public static byte[] Encode(
        ProtocolFrameTemplate template,
        ReadOnlySpan<byte> payload)
    {
        ValidatePayload(template, payload.Length);
        var checksumLength = GetChecksumLength(template.Checksum);
        var frame = new byte[template.Header.Length + payload.Length + checksumLength + template.Footer.Length];
        var offset = 0;
        template.Header.CopyTo(frame, offset);
        offset += template.Header.Length;
        payload.CopyTo(frame.AsSpan(offset));
        offset += payload.Length;
        if (template.Checksum != ProtocolChecksum.None)
        {
            var checksum = Calculate(template.Checksum, frame.AsSpan(0, offset));
            if (template.Checksum == ProtocolChecksum.Xor8)
            {
                frame[offset++] = (byte)checksum;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset, 2), checksum);
                offset += 2;
            }
        }

        template.Footer.CopyTo(frame, offset);
        return frame;
    }

    public static byte[] Decode(
        ProtocolFrameTemplate template,
        ReadOnlySpan<byte> frame)
    {
        var checksumLength = GetChecksumLength(template.Checksum);
        var payloadOffset = template.Header.Length;
        var payloadLength = frame.Length - template.Header.Length - template.Footer.Length - checksumLength;
        var hasValidHeader = frame.Length >= template.Header.Length &&
                             frame[..template.Header.Length].SequenceEqual(template.Header);
        var hasValidFooter = template.Footer.Length == 0 ||
                             (frame.Length >= template.Footer.Length &&
                              frame[^template.Footer.Length..].SequenceEqual(template.Footer));
        if (payloadLength < 0 || !hasValidHeader || !hasValidFooter)
        {
            throw new InvalidDataException("Protocol frame header or footer is invalid.");
        }

        ValidatePayload(template, payloadLength);
        if (template.Checksum != ProtocolChecksum.None)
        {
            var checksumOffset = payloadOffset + payloadLength;
            var expected = template.Checksum == ProtocolChecksum.Xor8
                ? frame[checksumOffset]
                : BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(checksumOffset, 2));
            var actual = Calculate(template.Checksum, frame[..(payloadOffset + payloadLength)]);
            if (expected != actual)
            {
                throw new InvalidDataException("Protocol frame checksum is invalid.");
            }
        }

        return frame.Slice(payloadOffset, payloadLength).ToArray();
    }

    private static int GetChecksumLength(ProtocolChecksum checksum) =>
        checksum switch
        {
            ProtocolChecksum.None => 0,
            ProtocolChecksum.Xor8 => 1,
            ProtocolChecksum.Crc16Modbus => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(checksum))
        };

    private static void ValidatePayload(ProtocolFrameTemplate template, int length)
    {
        if (length < template.MinimumPayloadLength || length > template.MaximumPayloadLength)
        {
            throw new InvalidDataException(
                $"Protocol payload length {length} is outside the configured range.");
        }
    }

    private static ushort Calculate(ProtocolChecksum checksum, ReadOnlySpan<byte> bytes)
    {
        if (checksum == ProtocolChecksum.Crc16Modbus)
        {
            return CalculateCrc16(bytes);
        }

        if (checksum == ProtocolChecksum.Xor8)
        {
            byte value = 0;
            foreach (var current in bytes)
            {
                value ^= current;
            }

            return value;
        }

        return 0;
    }

    private static ushort CalculateCrc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? (ushort)(crc >> 1) : (ushort)((crc >> 1) ^ 0xA001);
            }
        }

        return crc;
    }
}
