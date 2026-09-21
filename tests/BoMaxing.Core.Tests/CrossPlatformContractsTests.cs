using System.Text.Json;
using BoMaxing.Core.Devices;
using BoMaxing.Core.Devices.Genicam;
using BoMaxing.Core.Devices.Native;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Tests;

public sealed class CrossPlatformContractsTests
{
    [Fact]
    public void Image_frame_exposes_metadata_and_rows_without_copying()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        var metadata = new FrameMetadata(
            "camera-1",
            Sequence: 7,
            DateTimeOffset.UnixEpoch,
            Width: 3,
            Height: 2,
            Stride: 3,
            PixelFormat.Gray8);
        var frame = new ImageFrame(metadata, data);

        Assert.Equal([1, 2, 3], frame.GetRow(0).ToArray());
        Assert.Equal([4, 5, 6], frame.GetRow(1).ToArray());
        Assert.Equal(7, frame.Metadata.Sequence);
    }

    [Fact]
    public void Depth_and_point_cloud_preserve_coordinate_system_and_scale()
    {
        var depth = new DepthMap(
            2,
            2,
            [100, 200, 300, 400],
            unitScale: 0.001,
            coordinateSystem: CoordinateSystem.World);
        var pointCloud = new PointCloud3D(
            [1f, 2f, 3f, 4f, 5f, 6f],
            [10f, 20f],
            CoordinateSystem.Robot);

        Assert.Equal(0.001, depth.UnitScale);
        Assert.Equal(CoordinateSystem.World, depth.CoordinateSystem);
        Assert.Equal((4f, 5f, 6f), pointCloud.GetPoint(1));
        Assert.Equal(CoordinateSystem.Robot, pointCloud.CoordinateSystem);
    }

    [Fact]
    public void Point_cloud_processing_filters_and_measures_bounds()
    {
        var pointCloud = new PointCloud3D(
            [0, 0, 1, 1, 2, 3, 4, 5, 8],
            [10, 20, 30]);

        var filtered = PointCloudProcessing.FilterByZ(pointCloud, 2, 8);
        var bounds = PointCloudProcessing.GetBounds(filtered);

        Assert.Equal(2, filtered.Count);
        Assert.Equal((1f, 2f, 3f), filtered.GetPoint(0));
        Assert.Equal(4f, bounds.MaxX);
        Assert.Equal(8f, bounds.MaxZ);
    }

    [Fact]
    public void Frame_preview_summarizes_image_depth_and_point_cloud()
    {
        var image = Image2D.From8Bit(2, 2, [0, 10, 20, 30]);
        var imageSummary = FramePreview.Summarize(image);
        var depth = new DepthMap(2, 1, [100, 300], unitScale: 0.001);
        var depthSummary = FramePreview.Summarize(depth);
        var pointCloud = new PointCloud3D([0, 0, 1, 2, 3, 4]);
        var cloudSummary = FramePreview.Summarize(pointCloud);

        Assert.Equal(0, imageSummary.Minimum);
        Assert.Equal(30, imageSummary.Maximum);
        Assert.Equal(15, imageSummary.Mean);
        Assert.Equal(0.1, depthSummary.Minimum);
        Assert.Equal(0.3, depthSummary.Maximum);
        Assert.Equal(2, cloudSummary.PointCount);
    }

    [Fact]
    public async Task Camera_recording_roundtrips_and_replays_frames()
    {
        var metadata = new FrameMetadata(
            "recorded-camera",
            8,
            DateTimeOffset.UtcNow,
            2,
            1,
            2,
            PixelFormat.Gray8);
        var cameraFrame = new CameraFrame(
            metadata,
            image: new ImageFrame(metadata, new byte[] { 10, 20 }));
        var recording = CameraFrameRecording.FromFrames([cameraFrame], "Test Recording");
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.recording.json");

        try
        {
            var store = new CameraFrameRecordingStore();
            await store.SaveAsync(recording, path);
            var loaded = await store.LoadAsync(path);
            await using var session = new RecordedCameraSession(
                new DeviceConfiguration { Name = "Replay", TypeId = "replay.camera" },
                loaded.Frames.Select(frame => frame.ToFrame()),
                loop: false);
            await session.ConnectAsync();
            var replayed = await session.CaptureAsync(new CaptureRequest());

            Assert.Equal("Test Recording", loaded.Name);
            Assert.Equal(8, replayed.Metadata.Sequence);
            Assert.Equal([10, 20], replayed.Image!.Data.ToArray());
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => session.CaptureAsync(new CaptureRequest()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Device_registry_creates_a_camera_session_from_a_plugin()
    {
        var registry = new DevicePluginRegistry();
        var plugin = new FakeCameraPlugin();
        registry.Register(plugin);

        await using var session = await registry.CreateCameraSessionAsync(
            new DeviceConfiguration
            {
                TypeId = plugin.Descriptor.TypeId,
                Name = "Test Camera"
            });

        Assert.Equal("Test Camera", session.Configuration.Name);
        Assert.True(session.Capabilities.SupportsPointCloud);
    }

    [Fact]
    public async Task Recorded_camera_plugin_loads_configuration_and_replays_frame()
    {
        var metadata = new FrameMetadata(
            "recorded-camera",
            3,
            DateTimeOffset.UnixEpoch,
            1,
            1,
            1,
            PixelFormat.Gray8);
        var recording = CameraFrameRecording.FromFrames(
            [new CameraFrame(metadata, new ImageFrame(metadata, new byte[] { 42 }))]);
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.recording.json");

        try
        {
            await new CameraFrameRecordingStore().SaveAsync(recording, path);
            var plugin = new RecordedCameraPlugin();
            await using var session = (ICameraSession)await plugin.CreateSessionAsync(
                new DeviceConfiguration
                {
                    Name = "Replay Camera",
                    TypeId = plugin.Descriptor.TypeId,
                    Settings = new Dictionary<string, JsonElement>
                    {
                        ["recordingPath"] = JsonSerializer.SerializeToElement(path),
                        ["loop"] = JsonSerializer.SerializeToElement(false)
                    }
                });

            await session.ConnectAsync();
            var frame = await session.CaptureAsync(new CaptureRequest());

            Assert.Equal(3, frame.Metadata.Sequence);
            Assert.Equal([42], frame.Image!.Data.ToArray());
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => session.CaptureAsync(new CaptureRequest()));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Image_decoder_registry_routes_by_extension()
    {
        var registry = new ImageDecoderRegistry();
        registry.Register(new PgmImageDecoder());
        registry.Register(new BmpImageDecoder());
        registry.Register(new PngImageDecoder());
        registry.Register(new TiffImageDecoder());

        Assert.Equal("pgm", registry.Get("sample.PGM").FormatId);
        Assert.Equal("bmp", registry.Get("sample.bmp").FormatId);
        Assert.Equal("png", registry.Get("sample.png").FormatId);
        Assert.Equal("tiff", registry.Get("sample.tiff").FormatId);
    }

    [Fact]
    public async Task Genicam_plugin_wraps_transport_and_forwards_features_and_capture()
    {
        var transport = new FakeGenICamTransport();
        var plugin = new GenICamCameraPlugin((_, _) =>
            Task.FromResult<IGenICamTransport>(transport));
        var configuration = new DeviceConfiguration
        {
            Name = "GenICam Test Camera",
            TypeId = plugin.Descriptor.TypeId
        };
        await using var session = (GenICamCameraSession)await plugin.CreateSessionAsync(configuration);

        await session.ConnectAsync();
        await session.SetFeatureAsync("ExposureTime", "1200");
        var features = await session.GetFeaturesAsync();
        var frame = await session.CaptureAsync(new CaptureRequest());

        Assert.Equal(DeviceState.Connected, session.State);
        Assert.Equal("1200", transport.Features["ExposureTime"]);
        Assert.Contains(features, feature => feature.Name == "ExposureTime");
        Assert.Equal("genicam-1", frame.Metadata.DeviceId);
        await session.DisconnectAsync();
        Assert.Equal(DeviceState.Disconnected, session.State);
    }

    [Fact]
    public async Task Device_session_manager_connects_and_forwards_feature_operations()
    {
        var plugin = new FakeFeatureCameraPlugin();
        var registry = new DevicePluginRegistry();
        registry.Register(plugin);
        await using var manager = new DeviceSessionManager(registry);
        var configuration = new DeviceConfiguration
        {
            Id = "camera-1",
            Name = "Feature Camera",
            TypeId = plugin.Descriptor.TypeId
        };
        manager.Configure([configuration]);

        var features = await manager.GetFeaturesAsync("camera-1");
        await manager.SetFeatureAsync("camera-1", "Gain", "4.5");

        Assert.Equal(DeviceState.Connected, manager.GetState("camera-1"));
        Assert.Contains(features, feature => feature.Name == "Gain");
        Assert.Equal("4.5", plugin.Session.Features["Gain"]);
        Assert.Equal("4.5", configuration.FeatureValues["Gain"]);
        await manager.DisconnectAsync("camera-1");
        Assert.Equal(DeviceState.Disconnected, manager.GetState("camera-1"));
        plugin.Session.Features.Clear();
        await manager.ConnectAsync("camera-1");
        Assert.Equal("4.5", plugin.Session.Features["Gain"]);
    }

    [Fact]
    public async Task Native_ipc_codec_roundtrips_header_and_payload()
    {
        var encoded = NativeIpcCodec.Encode(
            NativeIpcMessageType.CaptureRequest,
            requestId: 17,
            payload: new byte[] { 1, 2, 3 });

        await using var stream = new MemoryStream();
        await stream.WriteAsync(encoded);
        stream.Position = 0;
        var message = await NativeIpcCodec.ReadAsync(stream);

        Assert.Equal(NativeIpcMessageType.CaptureRequest, message.Header.MessageType);
        Assert.Equal((uint)17, message.Header.RequestId);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload);
    }

    [Fact]
    public void Native_ipc_codec_rejects_invalid_magic_and_payload_size()
    {
        var encoded = NativeIpcCodec.Encode(
            NativeIpcMessageType.PingRequest,
            requestId: 1,
            payload: []);
        encoded[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => NativeIpcCodec.DecodeHeader(encoded));

        var oversized = NativeIpcCodec.Encode(
            NativeIpcMessageType.PingRequest,
            requestId: 1,
            payload: [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() =>
            NativeIpcCodec.DecodeHeader(oversized, maxPayloadLength: 2));
    }

    [Fact]
    public async Task Native_ipc_client_validates_requests_and_decodes_driver_responses()
    {
        await using var stream = new LoopbackIpcStream();
        await using var client = new NativeIpcClient(stream);

        Assert.Equal((uint)1, await client.GetApiVersionAsync());
        var manifest = await client.GetManifestAsync();
        var frame = await client.CaptureAsync();

        Assert.Equal("loopback", manifest.PluginId);
        Assert.Equal("loopback-camera", frame.Metadata.DeviceId);
        Assert.Equal(new byte[] { 42 }, frame.Image!.Data.ToArray());
    }

    [Fact]
    public async Task Native_ipc_client_supports_ping_and_feature_control()
    {
        await using var stream = new LoopbackIpcStream();
        await using var client = new NativeIpcClient(stream);

        await client.PingAsync();
        var features = await client.GetFeaturesAsync();
        await client.SetFeatureAsync("Gain", "2.5");

        Assert.Contains(features, feature => feature.Name == "Gain");
    }

    [Fact]
    public void Shared_frame_pool_publishes_reads_and_rejects_stale_lease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.frames");
        try
        {
            using var pool = new SharedFramePool(new SharedFramePoolOptions(path, 2, 64));
            var first = pool.Publish(0, [10, 20, 30]);
            Assert.Equal(new byte[] { 10, 20, 30 }, pool.Read(first));

            var second = pool.Publish(0, [40]);
            Assert.Throws<InvalidDataException>(() => pool.Read(first));
            Assert.Equal(new byte[] { 40 }, pool.Read(second));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeCameraPlugin : IDevicePlugin
    {
        public DeviceDescriptor Descriptor { get; } = new(
            "test.camera",
            "Test Camera",
            DeviceKind.Camera3D,
            ["capture", "pointCloud"]);

        public Task<IDeviceSession> CreateSessionAsync(
            DeviceConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IDeviceSession>(new FakeCameraSession(configuration));
    }

    private sealed class FakeFeatureCameraPlugin : IDevicePlugin
    {
        public DeviceDescriptor Descriptor { get; } = new(
            "test.feature-camera",
            "Feature Camera",
            DeviceKind.Camera3D,
            ["capture", "featureControl"]);

        public FakeFeatureCameraSession Session { get; } = new();

        public Task<IDeviceSession> CreateSessionAsync(
            DeviceConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Session.Configuration = configuration;
            return Task.FromResult<IDeviceSession>(Session);
        }
    }

    private sealed class FakeFeatureCameraSession : ICameraSession, IDeviceFeatureSession
    {
        public DeviceConfiguration Configuration { get; set; } = new();
        public DeviceState State { get; private set; } = DeviceState.Created;
        public CameraCapabilities Capabilities { get; } = new(
            true,
            false,
            false,
            [PixelFormat.Gray8],
            ["featureControl"]);
        public Dictionary<string, string> Features { get; } = new() { ["Gain"] = "1.0" };

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceFeature>>(
                Features.Select(item => new DeviceFeature(item.Key, item.Value)).ToArray());

        public Task SetFeatureAsync(
            string name,
            string value,
            CancellationToken cancellationToken = default)
        {
            Features[name] = value;
            return Task.CompletedTask;
        }

        public Task<CameraFrame> CaptureAsync(
            CaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LoopbackIpcStream : Stream
    {
        private readonly object _lock = new();
        private byte[] _available = [];
        private TaskCompletionSource<bool> _dataReady =
            NewDataReadySource();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            EnqueueResponse(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task waitTask;
                lock (_lock)
                {
                    if (_available.Length > 0)
                    {
                        var count = Math.Min(buffer.Length, _available.Length);
                        _available.AsSpan(0, count).CopyTo(buffer.Span);
                        _available = _available[count..];
                        return count;
                    }

                    waitTask = _dataReady.Task;
                }

                await waitTask.WaitAsync(cancellationToken);
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueResponse(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void EnqueueResponse(ReadOnlySpan<byte> requestBytes)
        {
            var request = NativeIpcCodec.DecodeHeader(requestBytes);
            var responseType = request.MessageType switch
            {
                NativeIpcMessageType.PingRequest => NativeIpcMessageType.PingResponse,
                NativeIpcMessageType.ApiVersionRequest => NativeIpcMessageType.ApiVersionResponse,
                NativeIpcMessageType.ManifestRequest => NativeIpcMessageType.ManifestResponse,
                NativeIpcMessageType.CaptureRequest => NativeIpcMessageType.CaptureResponse,
                NativeIpcMessageType.FeaturesRequest => NativeIpcMessageType.FeaturesResponse,
                NativeIpcMessageType.SetFeatureRequest => NativeIpcMessageType.SetFeatureResponse,
                NativeIpcMessageType.QuitRequest => NativeIpcMessageType.QuitResponse,
                _ => NativeIpcMessageType.ErrorResponse
            };
            var payload = responseType switch
            {
                NativeIpcMessageType.PingResponse => "pong"u8.ToArray(),
                NativeIpcMessageType.ApiVersionResponse => BitConverter.GetBytes(1u),
                NativeIpcMessageType.ManifestResponse => JsonSerializer.SerializeToUtf8Bytes(
                    new NativePluginManifest(
                        "loopback",
                        "Loopback Camera",
                        "1.0",
                        DeviceKind.Camera3D,
                        NativePluginTransport.OutOfProcess)),
                NativeIpcMessageType.CaptureResponse => JsonSerializer.SerializeToUtf8Bytes(
                    new NativeFrameResponse(
                        "loopback-camera",
                        1,
                        0,
                        1,
                        1,
                        1,
                        PixelFormat.Gray8,
                        1,
                        CoordinateSystem.Camera,
                        "2A")),
                NativeIpcMessageType.FeaturesResponse => JsonSerializer.SerializeToUtf8Bytes(
                    new[] { new DeviceFeature("Gain", "1.0") }),
                NativeIpcMessageType.SetFeatureResponse => "ok"u8.ToArray(),
                _ => Array.Empty<byte>()
            };
            var response = NativeIpcCodec.Encode(responseType, request.RequestId, payload);
            lock (_lock)
            {
                _available = [.. _available, .. response];
                _dataReady.TrySetResult(true);
                _dataReady = NewDataReadySource();
            }
        }

        private static TaskCompletionSource<bool> NewDataReadySource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeCameraSession : ICameraSession
    {
        public FakeCameraSession(DeviceConfiguration configuration)
        {
            Configuration = configuration;
        }

        public DeviceConfiguration Configuration { get; }
        public DeviceState State { get; private set; } = DeviceState.Created;
        public CameraCapabilities Capabilities { get; } = new(
            Supports2D: true,
            SupportsDepth: true,
            SupportsPointCloud: true,
            [PixelFormat.Gray8, PixelFormat.Depth16, PixelFormat.Float32],
            ["capture", "pointCloud"]);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<CameraFrame> CaptureAsync(
            CaptureRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeGenICamTransport : IGenICamTransport
    {
        public GenICamDeviceInfo DeviceInfo { get; } = new(
            "BoMaxing",
            "GenICam Test",
            "GEN-001",
            "GigE",
            new Dictionary<string, string>());

        public CameraCapabilities Capabilities { get; } = new(
            true,
            true,
            true,
            [PixelFormat.Gray8, PixelFormat.Depth16],
            ["capture", "featureControl"]);

        public DeviceState State { get; private set; } = DeviceState.Created;
        public Dictionary<string, string> Features { get; } = [];

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = DeviceState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<CameraFrame> CaptureAsync(
            CaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            var metadata = new FrameMetadata(
                "genicam-1",
                1,
                DateTimeOffset.UnixEpoch,
                1,
                1,
                1,
                PixelFormat.Gray8);
            return Task.FromResult(new CameraFrame(
                metadata,
                image: new ImageFrame(metadata, new byte[] { 7 })));
        }

        public Task SetFeatureAsync(
            string name,
            string value,
            CancellationToken cancellationToken = default)
        {
            Features[name] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceFeature>>(
                Features.Select(item => new DeviceFeature(item.Key, item.Value)).ToArray());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
