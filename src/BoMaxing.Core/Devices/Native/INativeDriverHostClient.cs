using BoMaxing.Core.Devices;

namespace BoMaxing.Core.Devices.Native;

public interface INativeDriverHostClient : IAsyncDisposable
{
    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<uint> GetApiVersionAsync(CancellationToken cancellationToken = default);

    Task<NativePluginManifest> GetManifestAsync(
        CancellationToken cancellationToken = default);

    Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default);

    Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
