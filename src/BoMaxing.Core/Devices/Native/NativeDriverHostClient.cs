using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using BoMaxing.Core.Devices;
using BoMaxing.Core.Imaging;

namespace BoMaxing.Core.Devices.Native;

public sealed record NativeFrameResponse(
    string DeviceId,
    long Sequence,
    long TimestampUnixNs,
    int Width,
    int Height,
    int Stride,
    PixelFormat PixelFormat,
    double UnitScale,
    CoordinateSystem CoordinateSystem,
    string DataHex);

public sealed class NativeDriverHostClient : INativeDriverHostClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NativeDriverHostOptions _options;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private bool _disposed;

    public NativeDriverHostClient(NativeDriverHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        ResetProcess();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                Arguments = _options.Arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Native driver host could not be started.");
        }

        _process = process;
        _writer = process.StandardInput;
        _reader = process.StandardOutput;
        var timeout = _options.StartupTimeout ?? TimeSpan.FromSeconds(5);
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(timeout);
        try
        {
            var response = await SendRequestAsync(
                "ping",
                startupTimeout.Token,
                allowRestart: false);
            if (!string.Equals(response, "pong", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Native driver host returned an invalid ping response.");
            }
        }
        catch
        {
            ResetProcess();
            throw;
        }
    }

    public async Task<uint> GetApiVersionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var response = await SendRequestAsync("api_version", cancellationToken);
        if (!uint.TryParse(response, out var version))
        {
            throw new InvalidDataException("Native driver host returned an invalid API version.");
        }

        return version;
    }

    public async Task<NativePluginManifest> GetManifestAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var response = await SendRequestAsync("manifest", cancellationToken);
        return JsonSerializer.Deserialize<NativePluginManifest>(response, JsonOptions)
            ?? throw new InvalidDataException("Native driver host returned an empty manifest.");
    }

    public async Task<CameraFrame> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var response = await SendRequestAsync("capture", cancellationToken);
        var frame = JsonSerializer.Deserialize<NativeFrameResponse>(response, JsonOptions)
            ?? throw new InvalidDataException("Native driver host returned an empty frame.");
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
        var image = frame.PixelFormat == PixelFormat.Gray8
            ? new ImageFrame(metadata, bytes)
            : null;
        return new CameraFrame(metadata, image: image);
    }

    public async Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var response = await SendRequestAsync("features", cancellationToken);
        var features = JsonSerializer.Deserialize<List<DeviceFeature>>(response, JsonOptions);
        return features ?? throw new InvalidDataException(
            "Native driver host returned an empty feature list.");
    }

    public async Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        await EnsureStartedAsync(cancellationToken);
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(name));
        var encodedValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        var response = await SendRequestAsync(
            $"set_feature|{encodedName}|{encodedValue}",
            cancellationToken);
        if (!string.Equals(response, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Native driver host returned an invalid feature response: {response}");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_process is null)
            {
                return;
            }

            try
            {
                if (!_process.HasExited && _writer is not null)
                {
                    await SendRequestAsync(
                        "quit",
                        cancellationToken,
                        allowRestart: false);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                ResetProcess();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _requestLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            await StartAsync(cancellationToken);
        }
    }

    private async Task<string> SendRequestAsync(
        string command,
        CancellationToken cancellationToken,
        bool allowRestart = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _requestLock.WaitAsync(cancellationToken);
                try
                {
                    return await SendRequestCoreAsync(command, cancellationToken);
                }
                finally
                {
                    _requestLock.Release();
                }
            }
            catch (Exception exception) when (
                attempt == 0 &&
                allowRestart &&
                _options.RestartOnCrash &&
                !_disposed &&
                IsProcessFailure(exception))
            {
                await RestartAfterCrashAsync(cancellationToken);
            }
        }
    }

    private async Task<string> SendRequestCoreAsync(
        string command,
        CancellationToken cancellationToken)
    {
        if (_writer is null || _reader is null || !IsRunning)
        {
            throw new InvalidOperationException("Native driver host is not running.");
        }

        await _writer.WriteLineAsync(command.AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
        var response = await _reader.ReadLineAsync(cancellationToken);
        if (response is null)
        {
            throw new EndOfStreamException("Native driver host closed its output stream.");
        }

        if (response.StartsWith("{\"error\"", StringComparison.Ordinal))
        {
            throw new IOException($"Native driver host rejected '{command}': {response}");
        }

        return response;
    }

    private async Task RestartAfterCrashAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ResetProcess();
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void ResetProcess()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }

        _process = null;
        _writer = null;
        _reader = null;
    }

    private static bool IsProcessFailure(Exception exception) =>
        exception is EndOfStreamException or IOException or InvalidOperationException;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
