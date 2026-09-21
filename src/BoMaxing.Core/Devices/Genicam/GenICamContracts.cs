using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Devices.Genicam;

public sealed record GenICamDeviceInfo(
    string Vendor,
    string Model,
    string SerialNumber,
    string TransportLayer,
    IReadOnlyDictionary<string, string> Features);

public interface IGenICamTransport : IAsyncDisposable
{
    GenICamDeviceInfo DeviceInfo { get; }
    CameraCapabilities Capabilities { get; }
    DeviceState State { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<CameraFrame> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default);
    Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default);
}

public sealed class GenICamCameraPlugin : IDevicePlugin
{
    private readonly Func<DeviceConfiguration, CancellationToken, Task<IGenICamTransport>> _factory;

    public GenICamCameraPlugin(
        Func<DeviceConfiguration, CancellationToken, Task<IGenICamTransport>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public DeviceDescriptor Descriptor { get; } = new(
        "genicam.camera",
        "GenICam Camera",
        DeviceKind.Camera3D,
        ["capture", "featureControl", "image", "depth", "pointCloud"]);

    public async Task<IDeviceSession> CreateSessionAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.TypeId.Equals(Descriptor.TypeId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration type '{configuration.TypeId}' does not match '{Descriptor.TypeId}'.");
        }

        var transport = await _factory(configuration, cancellationToken);
        return new GenICamCameraSession(configuration, transport);
    }
}

public sealed class GenICamCameraSession : ICameraSession, IDeviceFeatureSession
{
    private readonly IGenICamTransport _transport;
    private DeviceState _state = DeviceState.Created;

    public GenICamCameraSession(
        DeviceConfiguration configuration,
        IGenICamTransport transport)
    {
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        DeviceInfo = transport.DeviceInfo;
    }

    public DeviceConfiguration Configuration { get; }
    public GenICamDeviceInfo DeviceInfo { get; }
    public DeviceState State => _state;
    public CameraCapabilities Capabilities => _transport.Capabilities;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == DeviceState.Connected)
        {
            return;
        }

        _state = DeviceState.Connecting;
        try
        {
            await _transport.ConnectAsync(cancellationToken);
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
        await _transport.DisconnectAsync(cancellationToken);
        _state = DeviceState.Disconnected;
    }

    public Task<CameraFrame> CaptureAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_state != DeviceState.Connected)
        {
            throw new InvalidOperationException("GenICam camera session is not connected.");
        }

        return _transport.CaptureAsync(request, cancellationToken);
    }

    public Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (_state != DeviceState.Connected)
        {
            throw new InvalidOperationException("GenICam camera session is not connected.");
        }

        return _transport.SetFeatureAsync(name, value, cancellationToken);
    }

    public Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default) =>
        _transport.GetFeaturesAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _transport.DisposeAsync();
        _state = DeviceState.Disconnected;
    }
}
