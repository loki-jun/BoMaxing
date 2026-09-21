namespace BoMaxing.Core.Communication;

public sealed class ModbusClientRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, ModbusTcpClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    public void Register(string id, ModbusTcpClient client)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(client);

        lock (_lock)
        {
            if (!_clients.TryAdd(id, client))
            {
                throw new InvalidOperationException(
                    $"Modbus client '{id}' is already registered.");
            }
        }
    }

    public ModbusTcpClient Get(string id)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            return _clients.TryGetValue(id, out var client)
                ? client
                : throw new KeyNotFoundException(
                    $"Modbus client '{id}' is not registered.");
        }
    }

    public IReadOnlyList<string> GetIds()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            return _clients.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ModbusTcpClient[] clients;
        lock (_lock)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
