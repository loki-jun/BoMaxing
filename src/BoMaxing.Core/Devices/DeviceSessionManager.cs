using System.Text.Json;

namespace BoMaxing.Core.Devices;

public sealed class DeviceSessionManager : IAsyncDisposable
{
    private readonly DevicePluginRegistry _plugins;
    private readonly Dictionary<string, DeviceConfiguration> _configurations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDeviceSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DeviceSessionManager(DevicePluginRegistry plugins)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
    }

    public void Configure(IEnumerable<DeviceConfiguration> configurations)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(configurations);
        _gate.Wait();
        try
        {
            _configurations.Clear();
            foreach (var configuration in configurations)
            {
                ArgumentNullException.ThrowIfNull(configuration);
                if (string.IsNullOrWhiteSpace(configuration.Id))
                {
                    throw new InvalidDataException("Device configuration must have an ID.");
                }

                _configurations[configuration.Id] = configuration;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CameraFrame> CaptureAsync(
        string deviceId,
        CaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        var camera = await GetCameraSessionAsync(deviceId, cancellationToken);
        return await camera.CaptureAsync(request, cancellationToken);
    }

    public async Task ConnectAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await GetSessionAsync(deviceId, cancellationToken);
    }

    public async Task DisconnectAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.TryGetValue(deviceId, out var session))
            {
                await session.DisconnectAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public DeviceState GetState(string deviceId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _gate.Wait();
        try
        {
            return _sessions.TryGetValue(deviceId, out var session)
                ? session.State
                : DeviceState.Created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ICameraSession> GetCameraSessionAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(deviceId, cancellationToken);
        return session as ICameraSession
            ?? throw new InvalidOperationException(
                $"Device '{deviceId}' is not a camera session.");
    }

    public async Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetFeatureSessionAsync(deviceId, cancellationToken);
        return await session.GetFeaturesAsync(cancellationToken);
    }

    public async Task SetFeatureAsync(
        string deviceId,
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var session = await GetFeatureSessionAsync(deviceId, cancellationToken);
        await session.SetFeatureAsync(name, value, cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_configurations.TryGetValue(deviceId, out var configuration))
            {
                configuration.FeatureValues[name] = value;
                configuration.Settings[$"feature.{name}"] =
                    JsonSerializer.SerializeToElement(value);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IDeviceSession> GetSessionAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_configurations.TryGetValue(deviceId, out var configuration))
            {
                throw new KeyNotFoundException(
                    $"Device configuration '{deviceId}' is not registered.");
            }

            if (_sessions.TryGetValue(deviceId, out var existing))
            {
                if (existing.State != DeviceState.Connected)
                {
                    await existing.ConnectAsync(cancellationToken);
                    await ApplyConfiguredFeaturesAsync(existing, configuration, cancellationToken);
                }

                return existing;
            }

            var session = await _plugins.CreateSessionAsync(configuration, cancellationToken);
            _sessions[deviceId] = session;
            await session.ConnectAsync(cancellationToken);
            await ApplyConfiguredFeaturesAsync(session, configuration, cancellationToken);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IDeviceFeatureSession> GetFeatureSessionAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(deviceId, cancellationToken);
        return session as IDeviceFeatureSession
            ?? throw new InvalidOperationException(
                $"Device '{deviceId}' does not expose configurable features.");
    }

    private static async Task ApplyConfiguredFeaturesAsync(
        IDeviceSession session,
        DeviceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (session is not IDeviceFeatureSession featureSession ||
            configuration.FeatureValues.Count == 0)
        {
            return;
        }

        foreach (var feature in configuration.FeatureValues)
        {
            await featureSession.SetFeatureAsync(
                feature.Key,
                feature.Value,
                cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync();
        try
        {
            foreach (var session in _sessions.Values)
            {
                await session.DisposeAsync();
            }

            _sessions.Clear();
            _configurations.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
