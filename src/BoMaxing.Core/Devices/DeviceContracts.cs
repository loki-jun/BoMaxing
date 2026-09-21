using System.Text.Json;

namespace BoMaxing.Core.Devices;

public enum DeviceKind
{
    Camera2D,
    Camera3D,
    Light,
    DigitalIo,
    Plc,
    Robot,
    Generic
}

public enum DeviceState
{
    Created,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Faulted
}

public sealed record DeviceDescriptor(
    string TypeId,
    string DisplayName,
    DeviceKind Kind,
    IReadOnlyList<string> Capabilities);

public sealed class DeviceConfiguration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Device";
    public string TypeId { get; set; } = string.Empty;
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
    public Dictionary<string, string> FeatureValues { get; set; } = [];
}

public sealed record DeviceFeature(
    string Name,
    string Value,
    bool IsReadOnly = false);

public interface IDeviceSession : IAsyncDisposable
{
    DeviceConfiguration Configuration { get; }
    DeviceState State { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceFeatureSession
{
    Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default);

    Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default);
}

public interface IDevicePlugin
{
    DeviceDescriptor Descriptor { get; }

    Task<IDeviceSession> CreateSessionAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class DevicePluginRegistry
{
    private readonly Dictionary<string, IDevicePlugin> _plugins =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(IDevicePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var typeId = plugin.Descriptor.TypeId;
        if (!_plugins.TryAdd(typeId, plugin))
        {
            throw new InvalidOperationException(
                $"Device plugin '{typeId}' is already registered.");
        }
    }

    public IDevicePlugin Get(string typeId)
    {
        if (!_plugins.TryGetValue(typeId, out var plugin))
        {
            throw new KeyNotFoundException(
                $"Device plugin '{typeId}' is not registered.");
        }

        return plugin;
    }

    public Task<IDeviceSession> CreateSessionAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Get(configuration.TypeId)
            .CreateSessionAsync(configuration, cancellationToken);
    }

    public async Task<ICameraSession> CreateCameraSessionAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var session = await CreateSessionAsync(configuration, cancellationToken);
        return session as ICameraSession
            ?? throw new InvalidOperationException(
                $"Device plugin '{configuration.TypeId}' did not create a camera session.");
    }

    public IReadOnlyList<DeviceDescriptor> DescribeAll() =>
        _plugins.Values
            .Select(plugin => plugin.Descriptor)
            .OrderBy(descriptor => descriptor.Kind)
            .ThenBy(descriptor => descriptor.DisplayName)
            .ToArray();
}
