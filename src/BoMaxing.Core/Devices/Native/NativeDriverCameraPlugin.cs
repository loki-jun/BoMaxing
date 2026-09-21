using System.Text.Json;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Devices.Native;

public sealed class NativeDriverCameraPlugin : IDevicePlugin
{
    public DeviceDescriptor Descriptor { get; } = new(
        "native.camera3d",
        "Native Driver Camera",
        DeviceKind.Camera3D,
        ["capture", "image", "depth", "pointCloud"]);

    public Task<IDeviceSession> CreateSessionAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TypeId.Equals(Descriptor.TypeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration type '{configuration.TypeId}' does not match '{Descriptor.TypeId}'.");
        }

        var executablePath = ReadRequiredSetting(configuration, "executablePath");
        var arguments = ReadOptionalSetting(configuration, "arguments") ?? string.Empty;
        var protocol = ReadOptionalSetting(configuration, "protocol");
        INativeDriverHostClient host = string.Equals(
            protocol,
            "binary",
            StringComparison.OrdinalIgnoreCase)
            ? new NativeBinaryDriverHostClient(
                new NativeDriverHostOptions(executablePath, arguments))
            : new NativeDriverHostClient(
                new NativeDriverHostOptions(executablePath, arguments));
        return Task.FromResult<IDeviceSession>(
            new NativeDriverCameraSession(
                configuration,
                host));
    }

    private static string ReadRequiredSetting(
        DeviceConfiguration configuration,
        string name)
    {
        var value = ReadOptionalSetting(configuration, name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException(
                $"Native camera configuration requires '{name}'.");
    }

    private static string? ReadOptionalSetting(
        DeviceConfiguration configuration,
        string name)
    {
        return configuration.Settings.TryGetValue(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public sealed class NativeDriverCameraSession : ICameraSession, IDeviceFeatureSession
{
    private readonly INativeDriverHostClient _host;
    private DeviceState _state = DeviceState.Created;

    public NativeDriverCameraSession(
        DeviceConfiguration configuration,
        INativeDriverHostClient host)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public DeviceConfiguration Configuration { get; }
    public DeviceState State => _state;
    public CameraCapabilities Capabilities { get; } = new(
        Supports2D: true,
        SupportsDepth: false,
        SupportsPointCloud: false,
        [PixelFormat.Gray8],
        ["capture", "image"]);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == DeviceState.Connected)
        {
            return;
        }

        _state = DeviceState.Connecting;
        try
        {
            await _host.StartAsync(cancellationToken);
            _state = DeviceState.Connected;
        }
        catch
        {
            _state = DeviceState.Faulted;
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == DeviceState.Disconnected)
        {
            return;
        }

        _state = DeviceState.Disconnecting;
        await _host.StopAsync(cancellationToken);
        _state = DeviceState.Disconnected;
    }

    public async Task<CameraFrame> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_state != DeviceState.Connected)
        {
            throw new InvalidOperationException("Native camera session is not connected.");
        }

        var frame = await _host.CaptureAsync(cancellationToken);
        return frame;
    }

    public Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default) =>
        _host.GetFeaturesAsync(cancellationToken);

    public Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default) =>
        _host.SetFeatureAsync(name, value, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _state = DeviceState.Disconnected;
    }
}
