using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Devices.Native;

public sealed class NativeIpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _requestId;
    private bool _disposed;

    public NativeIpcClient(Stream stream)
        : this(stream, stream)
    {
    }

    public NativeIpcClient(Stream input, Stream output)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            NativeIpcMessageType.PingRequest,
            cancellationToken);
        if (response.Header.MessageType != NativeIpcMessageType.PingResponse ||
            !Encoding.UTF8.GetString(response.Payload).Equals(
                "pong",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native IPC ping response is invalid.");
        }
    }

    public async Task<uint> GetApiVersionAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            NativeIpcMessageType.ApiVersionRequest,
            cancellationToken);
        if (response.Header.MessageType != NativeIpcMessageType.ApiVersionResponse ||
            response.Payload.Length != sizeof(uint))
        {
            throw new InvalidDataException("Native IPC API version response is invalid.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(response.Payload);
    }

    public async Task<NativePluginManifest> GetManifestAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            NativeIpcMessageType.ManifestRequest,
            cancellationToken);
        if (response.Header.MessageType != NativeIpcMessageType.ManifestResponse)
        {
            throw new InvalidDataException("Native IPC manifest response is invalid.");
        }

        return JsonSerializer.Deserialize<NativePluginManifest>(response.Payload, JsonOptions)
            ?? throw new InvalidDataException("Native IPC manifest is empty.");
    }

    public async Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            NativeIpcMessageType.CaptureRequest,
            cancellationToken);
        if (response.Header.MessageType != NativeIpcMessageType.CaptureResponse)
        {
            throw new InvalidDataException("Native IPC capture response is invalid.");
        }

        var frame = JsonSerializer.Deserialize<NativeFrameResponse>(response.Payload, JsonOptions)
            ?? throw new InvalidDataException("Native IPC capture frame is empty.");
        var bytes = Convert.FromHexString(frame.DataHex);
        var metadata = new FrameMetadata(
            frame.DeviceId,
            frame.Sequence,
            DateTimeOffset.UnixEpoch.AddTicks(frame.TimestampUnixNs / 100),
            frame.Width,
            frame.Height,
            frame.Stride,
            frame.PixelFormat,
            frame.UnitScale,
            frame.CoordinateSystem);
        return new CameraFrame(
            metadata,
            image: frame.PixelFormat == PixelFormat.Gray8
                ? new ImageFrame(metadata, bytes)
                : null);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await SendAsync(NativeIpcMessageType.QuitRequest, cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            NativeIpcMessageType.FeaturesRequest,
            cancellationToken);
        if (response.Header.MessageType != NativeIpcMessageType.FeaturesResponse)
        {
            throw new InvalidDataException("Native IPC feature response is invalid.");
        }

        return JsonSerializer.Deserialize<List<DeviceFeature>>(
                   response.Payload,
                   JsonOptions)
               ?? throw new InvalidDataException("Native IPC features are empty.");
    }

    public async Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { name, value });
        var response = await SendAsync(
            NativeIpcMessageType.SetFeatureRequest,
            payload,
            cancellationToken);
        if (response.Header.MessageType != NativeIpcMessageType.SetFeatureResponse ||
            !Encoding.UTF8.GetString(response.Payload).Equals(
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Native IPC feature write response is invalid.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
        if (ReferenceEquals(_input, _output))
        {
            await _input.DisposeAsync();
        }
        else
        {
            await _input.DisposeAsync();
            await _output.DisposeAsync();
        }
    }

    private async Task<NativeIpcMessage> SendAsync(
        NativeIpcMessageType requestType,
        CancellationToken cancellationToken)
    {
        return await SendAsync(
            requestType,
            ReadOnlyMemory<byte>.Empty,
            cancellationToken);
    }

    private async Task<NativeIpcMessage> SendAsync(
        NativeIpcMessageType requestType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var requestId = unchecked(++_requestId);
            await NativeIpcCodec.WriteAsync(
                _output,
                requestType,
                requestId,
                payload,
                cancellationToken);
            var response = await NativeIpcCodec.ReadAsync(
                _input,
                cancellationToken: cancellationToken);
            if (response.Header.RequestId != requestId)
            {
                throw new InvalidDataException("Native IPC response request ID does not match.");
            }

            if (response.Header.MessageType == NativeIpcMessageType.ErrorResponse)
            {
                throw new IOException("Native IPC driver returned an error response.");
            }

            return response;
        }
        finally
        {
            _gate.Release();
        }
    }
}
