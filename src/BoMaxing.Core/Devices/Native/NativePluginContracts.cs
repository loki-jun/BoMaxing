namespace BoMaxing.Core.Devices.Native;

public static class NativePluginApi
{
    public const uint CurrentVersion = 1;
    public const string GetApiVersionExport = "bmx_get_api_version";
    public const string GetManifestJsonExport = "bmx_get_manifest_json";
}

public enum NativePluginTransport
{
    InProcess,
    OutOfProcess
}

public sealed record NativePluginManifest(
    string PluginId,
    string DisplayName,
    string Version,
    DeviceKind DeviceKind,
    NativePluginTransport Transport,
    string AbiVersion = "1");

public sealed record NativeDriverHostOptions
{
    public NativeDriverHostOptions(
        string executablePath,
        string arguments = "",
        TimeSpan? startupTimeout = null,
        bool restartOnCrash = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ExecutablePath = executablePath;
        Arguments = arguments;
        StartupTimeout = startupTimeout;
        RestartOnCrash = restartOnCrash;
    }

    public string ExecutablePath { get; }
    public string Arguments { get; }
    public TimeSpan? StartupTimeout { get; }
    public bool RestartOnCrash { get; }
}
