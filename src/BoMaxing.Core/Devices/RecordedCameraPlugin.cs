using System.Text.Json;

namespace BoMaxing.Core.Devices;

public sealed class RecordedCameraPlugin : IDevicePlugin
{
    public DeviceDescriptor Descriptor { get; } = new(
        "replay.camera",
        "Recorded Camera",
        DeviceKind.Camera3D,
        ["capture", "replay", "image", "depth", "pointCloud"]);

    public async Task<IDeviceSession> CreateSessionAsync(
        DeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration.Settings.TryGetValue("recordingPath", out var pathValue) &&
                   pathValue.ValueKind == JsonValueKind.String
            ? pathValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "Recorded camera configuration requires 'recordingPath'.");
        }

        var loop = !configuration.Settings.TryGetValue("loop", out var loopValue) ||
                   loopValue.ValueKind != JsonValueKind.False;
        var recording = await new CameraFrameRecordingStore().LoadAsync(path, cancellationToken);
        return new RecordedCameraSession(
            configuration,
            recording.Frames.Select(frame => frame.ToFrame()),
            loop);
    }
}
