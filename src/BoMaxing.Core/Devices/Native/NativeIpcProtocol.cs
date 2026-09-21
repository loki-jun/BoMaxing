using System.Buffers.Binary;

namespace BoMaxing.Core.Devices.Native;

public enum NativeIpcMessageType : ushort
{
    PingRequest = 1,
    PingResponse = 2,
    ApiVersionRequest = 3,
    ApiVersionResponse = 4,
    ManifestRequest = 5,
    ManifestResponse = 6,
    CaptureRequest = 7,
    CaptureResponse = 8,
    ErrorResponse = 9,
    QuitRequest = 10,
    QuitResponse = 11,
    FeaturesRequest = 12,
    FeaturesResponse = 13,
    SetFeatureRequest = 14,
    SetFeatureResponse = 15
}

public readonly record struct NativeIpcHeader(
    uint Magic,
    ushort Version,
    NativeIpcMessageType MessageType,
    uint PayloadLength,
    uint RequestId)
{
    public const int Size = 16;
    public const uint ExpectedMagic = 0x314D5842;
    public const ushort CurrentVersion = 1;

    public bool IsValid(int maxPayloadLength) =>
        Magic == ExpectedMagic &&
        Version == CurrentVersion &&
        PayloadLength <= maxPayloadLength;
}

public sealed record NativeIpcMessage(
    NativeIpcHeader Header,
    byte[] Payload);

public static class NativeIpcCodec
{
    public const int DefaultMaxPayloadLength = 64 * 1024 * 1024;

    public static byte[] Encode(
        NativeIpcMessageType messageType,
        uint requestId,
        ReadOnlySpan<byte> payload)
    {
        var frame = new byte[NativeIpcHeader.Size + payload.Length];
        WriteHeader(
            frame,
            new NativeIpcHeader(
                NativeIpcHeader.ExpectedMagic,
                NativeIpcHeader.CurrentVersion,
                messageType,
                checked((uint)payload.Length),
                requestId));
        payload.CopyTo(frame.AsSpan(NativeIpcHeader.Size));
        return frame;
    }

    public static NativeIpcHeader DecodeHeader(
        ReadOnlySpan<byte> bytes,
        int maxPayloadLength = DefaultMaxPayloadLength)
    {
        if (bytes.Length < NativeIpcHeader.Size)
        {
            throw new InvalidDataException("Native IPC header is incomplete.");
        }

        var header = new NativeIpcHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
            (NativeIpcMessageType)BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]));
        if (!header.IsValid(maxPayloadLength))
        {
            throw new InvalidDataException(
                $"Native IPC header is invalid: magic=0x{header.Magic:X8}, " +
                $"version={header.Version}, payload={header.PayloadLength}.");
        }

        return header;
    }

    public static async Task WriteAsync(
        Stream stream,
        NativeIpcMessageType messageType,
        uint requestId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = Encode(messageType, requestId, payload.Span);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<NativeIpcMessage> ReadAsync(
        Stream stream,
        int maxPayloadLength = DefaultMaxPayloadLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var headerBytes = new byte[NativeIpcHeader.Size];
        await stream.ReadExactlyAsync(headerBytes, cancellationToken);
        var header = DecodeHeader(headerBytes, maxPayloadLength);
        var payload = new byte[checked((int)header.PayloadLength)];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new NativeIpcMessage(header, payload);
    }

    private static void WriteHeader(Span<byte> destination, NativeIpcHeader header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, header.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], header.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)header.MessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], header.PayloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], header.RequestId);
    }
}
