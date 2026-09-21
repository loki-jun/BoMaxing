namespace BoMaxing.Core.Communication;

public sealed class CommunicationChannelRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, IByteChannel> _channels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public void Register(string id, IByteChannel channel)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(channel);

        lock (_lock)
        {
            if (!_channels.TryAdd(id, channel))
            {
                throw new InvalidOperationException(
                    $"Communication channel '{id}' is already registered.");
            }
        }
    }

    public IByteChannel Get(string id)
    {
        ThrowIfDisposed();
        if (!TryGet(id, out var channel))
        {
            throw new KeyNotFoundException(
                $"Communication channel '{id}' is not registered.");
        }

        return channel;
    }

    public bool TryGet(string id, out IByteChannel channel)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            return _channels.TryGetValue(id, out channel!);
        }
    }

    public Task ConnectAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        Get(id).ConnectAsync(cancellationToken);

    public Task DisconnectAsync(
        string id,
        CancellationToken cancellationToken = default) =>
        Get(id).DisconnectAsync(cancellationToken);

    public IReadOnlyList<string> GetIds()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            return _channels.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IByteChannel[] channels;
        lock (_lock)
        {
            channels = _channels.Values.ToArray();
            _channels.Clear();
        }

        foreach (var channel in channels)
        {
            await channel.DisposeAsync();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
