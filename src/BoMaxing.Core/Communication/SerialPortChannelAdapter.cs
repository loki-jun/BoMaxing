namespace BoMaxing.Core.Communication;

public sealed class SerialPortChannelAdapter : ISerialPortChannel
{
    private readonly Func<SerialPortSettings, CancellationToken, Task<Stream>> _openStream;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Stream? _stream;
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    public SerialPortChannelAdapter(
        SerialPortSettings settings,
        Func<SerialPortSettings, CancellationToken, Task<Stream>> openStream)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _openStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
    }

    public SerialPortSettings Settings { get; }
    public ConnectionState State => _state;

    public event EventHandler<ConnectionStateChanged>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_state == ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting);
            try
            {
                _stream = await _openStream(Settings, cancellationToken);
                SetState(ConnectionState.Connected);
            }
            catch (Exception exception)
            {
                _stream?.Dispose();
                _stream = null;
                SetState(ConnectionState.Faulted, exception);
                throw new CommunicationException(
                    $"Unable to open serial port '{Settings.PortName}'.",
                    exception);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_state == ConnectionState.Disconnected)
            {
                return;
            }

            SetState(ConnectionState.Disconnecting);
            _stream?.Dispose();
            _stream = null;
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        var stream = GetConnectedStream();
        try
        {
            await stream.WriteAsync(data, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            SetState(ConnectionState.Faulted, exception);
            throw new CommunicationException("Serial send failed.", exception);
        }
    }

    public async ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var stream = GetConnectedStream();
        try
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                SetState(ConnectionState.Disconnected);
            }

            return count;
        }
        catch (IOException exception)
        {
            SetState(ConnectionState.Faulted, exception);
            throw new CommunicationException("Serial receive failed.", exception);
        }
    }

    public async ValueTask<byte[]> ReceiveExactAsync(
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        var result = new byte[byteCount];
        var offset = 0;
        while (offset < result.Length)
        {
            var count = await ReceiveAsync(result.AsMemory(offset), cancellationToken);
            if (count == 0)
            {
                throw new CommunicationException(
                    $"Serial connection closed after receiving {offset} of {byteCount} bytes.");
            }

            offset += count;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleLock.WaitAsync();
        try
        {
            _stream?.Dispose();
            _stream = null;
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    private Stream GetConnectedStream()
    {
        ThrowIfDisposed();
        if (_state != ConnectionState.Connected || _stream is null)
        {
            throw new CommunicationException("Serial channel is not connected.");
        }

        return _stream;
    }

    private void SetState(ConnectionState state, Exception? exception = null)
    {
        var previous = _state;
        _state = state;
        if (previous != state || exception is not null)
        {
            StateChanged?.Invoke(
                this,
                new ConnectionStateChanged(previous, state, exception));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

