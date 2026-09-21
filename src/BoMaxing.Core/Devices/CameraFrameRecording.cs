using System.Text.Json;
using System.Text.Json.Serialization;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Devices;

public sealed class CameraFrameRecording
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Recording";
    public List<RecordedCameraFrame> Frames { get; set; } = [];

    public static CameraFrameRecording FromFrames(
        IEnumerable<CameraFrame> frames,
        string name = "Recording")
    {
        ArgumentNullException.ThrowIfNull(frames);
        return new CameraFrameRecording
        {
            Name = name,
            Frames = frames.Select(RecordedCameraFrame.FromFrame).ToList()
        };
    }
}

public sealed class RecordedCameraFrame
{
    public FrameMetadata Metadata { get; set; } = new(
        "unknown",
        0,
        DateTimeOffset.UnixEpoch,
        1,
        1,
        1,
        PixelFormat.Gray8);
    public string? ImageDataBase64 { get; set; }
    public ushort[]? DepthValues { get; set; }
    public float[]? PointPositions { get; set; }
    public float[]? PointIntensities { get; set; }

    public static RecordedCameraFrame FromFrame(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new RecordedCameraFrame
        {
            Metadata = frame.Metadata,
            ImageDataBase64 = frame.Image is null
                ? null
                : Convert.ToBase64String(frame.Image.Data.ToArray()),
            DepthValues = frame.Depth?.Values.ToArray(),
            PointPositions = frame.PointCloud?.Positions.ToArray(),
            PointIntensities = frame.PointCloud?.Intensities.IsEmpty == false
                ? frame.PointCloud.Intensities.ToArray()
                : null
        };
    }

    public CameraFrame ToFrame()
    {
        ImageFrame? image = null;
        if (!string.IsNullOrWhiteSpace(ImageDataBase64))
        {
            image = new ImageFrame(Metadata, Convert.FromBase64String(ImageDataBase64));
        }

        DepthMap? depth = null;
        if (DepthValues is not null)
        {
            depth = new DepthMap(
                Metadata.Width,
                Metadata.Height,
                DepthValues,
                Metadata.UnitScale,
                Metadata.CoordinateSystem);
        }

        PointCloud3D? pointCloud = null;
        if (PointPositions is not null)
        {
            pointCloud = new PointCloud3D(
                PointPositions,
                PointIntensities ?? [],
                Metadata.CoordinateSystem);
        }

        return new CameraFrame(Metadata, image, depth, pointCloud);
    }
}

public sealed class CameraFrameRecordingStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(
        CameraFrameRecording recording,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, recording, Options, cancellationToken);
    }

    public async Task<CameraFrameRecording> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<CameraFrameRecording>(
                   stream,
                   Options,
                   cancellationToken)
               ?? throw new InvalidDataException("Camera recording is empty.");
    }
}

public sealed class RecordedCameraSession : ICameraSession
{
    private readonly IReadOnlyList<CameraFrame> _frames;
    private readonly bool _loop;
    private int _index;
    private DeviceState _state = DeviceState.Created;

    public RecordedCameraSession(
        DeviceConfiguration configuration,
        IEnumerable<CameraFrame> frames,
        bool loop = true)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _frames = frames?.ToArray() ?? throw new ArgumentNullException(nameof(frames));
        if (_frames.Count == 0)
        {
            throw new ArgumentException("At least one recorded frame is required.", nameof(frames));
        }

        _loop = loop;
        Capabilities = new CameraCapabilities(
            _frames.Any(frame => frame.Image is not null),
            _frames.Any(frame => frame.Depth is not null),
            _frames.Any(frame => frame.PointCloud is not null),
            _frames.Select(frame => frame.Metadata.PixelFormat).Distinct().ToArray(),
            ["capture", "replay"]);
    }

    public DeviceConfiguration Configuration { get; }
    public DeviceState State => _state;
    public CameraCapabilities Capabilities { get; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = DeviceState.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = DeviceState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<CameraFrame> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_state != DeviceState.Connected)
        {
            throw new InvalidOperationException("Recorded camera session is not connected.");
        }

        if (_index >= _frames.Count)
        {
            if (!_loop)
            {
                throw new EndOfStreamException("Camera recording has reached its end.");
            }

            _index = 0;
        }

        return Task.FromResult(_frames[_index++]);
    }

    public ValueTask DisposeAsync()
    {
        _state = DeviceState.Disconnected;
        return ValueTask.CompletedTask;
    }
}
