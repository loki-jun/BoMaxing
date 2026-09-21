using System.Diagnostics;

namespace BoMaxing.Core.Devices.Native;

public sealed class NativeBinaryDriverHostClient : INativeDriverHostClient
{
    private readonly NativeDriverHostOptions _options;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Process? _process;
    private NativeIpcClient? _client;
    private bool _disposed;

    public NativeBinaryDriverHostClient(NativeDriverHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    public Task<uint> GetApiVersionAsync(CancellationToken cancellationToken = default) =>
        SendAsync(client => client.GetApiVersionAsync(cancellationToken), cancellationToken);

    public Task<NativePluginManifest> GetManifestAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(client => client.GetManifestAsync(cancellationToken), cancellationToken);

    public Task<CameraFrame> CaptureAsync(CancellationToken cancellationToken = default) =>
        SendAsync(client => client.CaptureAsync(cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DeviceFeature>> GetFeaturesAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync(client => client.GetFeaturesAsync(cancellationToken), cancellationToken);

    public Task SetFeatureAsync(
        string name,
        string value,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            client => client.SetFeatureAsync(name, value, cancellationToken),
            cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                if (IsRunning && _client is not null)
                {
                    await _client.CloseAsync(cancellationToken);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                await ResetAsync();
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
        _lifecycleLock.Dispose();
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        await ResetAsync();
        var arguments = string.IsNullOrWhiteSpace(_options.Arguments)
            ? "--ipc"
            : $"{_options.Arguments} --ipc";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                Arguments = arguments,
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
            throw new InvalidOperationException("Native binary driver host could not be started.");
        }

        _process = process;
        _client = new NativeIpcClient(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream);
        var timeout = _options.StartupTimeout ?? TimeSpan.FromSeconds(5);
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(timeout);
        try
        {
            await _client.PingAsync(startupTimeout.Token);
        }
        catch
        {
            await ResetAsync();
            throw;
        }
    }

    private async Task<T> SendAsync<T>(
        Func<NativeIpcClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var client = _client
                    ?? throw new InvalidOperationException("Native binary driver host is not running.");
                return await action(client);
            }
            catch (Exception exception) when (
                attempt == 0 &&
                _options.RestartOnCrash &&
                IsProcessFailure(exception))
            {
                await _lifecycleLock.WaitAsync(cancellationToken);
                try
                {
                    await ResetAsync();
                    await StartCoreAsync(cancellationToken);
                }
                finally
                {
                    _lifecycleLock.Release();
                }
            }
        }
    }

    private async Task SendAsync(
        Func<NativeIpcClient, Task> action,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        try
        {
            var client = _client
                ?? throw new InvalidOperationException("Native binary driver host is not running.");
            await action(client);
        }
        catch (Exception exception) when (
            _options.RestartOnCrash &&
            IsProcessFailure(exception))
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                await ResetAsync();
                await StartCoreAsync(cancellationToken);
                var client = _client
                    ?? throw new InvalidOperationException(
                        "Native binary driver host is not running after restart.");
                await action(client);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning)
        {
            await StartAsync(cancellationToken);
        }
    }

    private async Task ResetAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

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
            _process = null;
        }
    }

    private static bool IsProcessFailure(Exception exception) =>
        exception is EndOfStreamException or IOException or InvalidOperationException;
}
