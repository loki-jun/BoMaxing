using System.Net;
using System.Net.Sockets;

namespace BoMaxing.Core.Communication;

public sealed record TcpConnectionOptions
{
    public TcpConnectionOptions(
        string host,
        int port,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null,
        TimeSpan? writeTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Host = host;
        Port = port;
        ConnectTimeout = connectTimeout;
        ReadTimeout = readTimeout;
        WriteTimeout = writeTimeout;
    }

    public string Host { get; }
    public int Port { get; }
    public TimeSpan? ConnectTimeout { get; }
    public TimeSpan? ReadTimeout { get; }
    public TimeSpan? WriteTimeout { get; }
}

public sealed class TcpClientChannel : IByteChannel
{
    private readonly TcpConnectionOptions _options;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    public TcpClientChannel(TcpConnectionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

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
            var client = new TcpClient();
            using var timeout = CreateTimeoutToken(cancellationToken, _options.ConnectTimeout);
            try
            {
                await client.ConnectAsync(_options.Host, _options.Port, timeout.Token);
                var stream = client.GetStream();
                ApplyTimeouts(stream);
                _client = client;
                _stream = stream;
                SetState(ConnectionState.Connected);
            }
            catch (Exception exception)
            {
                client.Dispose();
                SetState(ConnectionState.Faulted, exception);
                throw new CommunicationException(
                    $"Unable to connect to TCP endpoint {_options.Host}:{_options.Port}.",
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
            _client?.Dispose();
            _stream = null;
            _client = null;
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
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CreateTimeoutToken(cancellationToken, _options.WriteTimeout);
            await stream.WriteAsync(data, timeout.Token);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            SetState(ConnectionState.Faulted, exception);
            throw new CommunicationException("TCP send failed.", exception);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var stream = GetConnectedStream();
        try
        {
            using var timeout = CreateTimeoutToken(cancellationToken, _options.ReadTimeout);
            var count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0)
            {
                SetState(ConnectionState.Disconnected);
            }

            return count;
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            SetState(ConnectionState.Faulted, exception);
            throw new CommunicationException("TCP receive failed.", exception);
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
                    $"TCP connection closed after receiving {offset} of {byteCount} bytes.");
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
            _client?.Dispose();
            _stream = null;
            _client = null;
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
            _sendLock.Dispose();
        }
    }

    private NetworkStream GetConnectedStream()
    {
        ThrowIfDisposed();
        if (_state != ConnectionState.Connected || _stream is null)
        {
            throw new CommunicationException("TCP channel is not connected.");
        }

        return _stream;
    }

    private CancellationTokenSource CreateTimeoutToken(
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            source.CancelAfter(timeout.Value);
        }

        return source;
    }

    private void ApplyTimeouts(NetworkStream stream)
    {
        if (_options.ReadTimeout.HasValue)
        {
            stream.ReadTimeout = (int)_options.ReadTimeout.Value.TotalMilliseconds;
        }

        if (_options.WriteTimeout.HasValue)
        {
            stream.WriteTimeout = (int)_options.WriteTimeout.Value.TotalMilliseconds;
        }
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
