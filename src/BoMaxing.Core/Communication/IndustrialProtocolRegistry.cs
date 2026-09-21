namespace BoMaxing.Core.Communication;

public sealed class IndustrialProtocolRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, IIndustrialProtocolSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public void Register(string id, IIndustrialProtocolSession session)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            if (!_sessions.TryAdd(id, session))
            {
                throw new InvalidOperationException(
                    $"Industrial protocol session '{id}' is already registered.");
            }
        }
    }

    public IIndustrialProtocolSession Get(string id)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            return _sessions.TryGetValue(id, out var session)
                ? session
                : throw new KeyNotFoundException(
                    $"Industrial protocol session '{id}' is not registered.");
        }
    }

    public async Task RegisterAndConnectAsync(
        string id,
        IIndustrialProtocolSession session,
        CancellationToken cancellationToken = default)
    {
        Register(id, session);
        await session.ConnectAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IIndustrialProtocolSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
