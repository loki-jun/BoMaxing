namespace BoMaxing.Core.Communication;

public enum IndustrialValueType
{
    Boolean,
    Int32,
    UInt16,
    Float64,
    Text,
    Bytes
}

public sealed record IndustrialVariable(
    string Address,
    IndustrialValueType ValueType,
    object? Value = null,
    string? Quality = null,
    DateTimeOffset? Timestamp = null);

public interface IIndustrialProtocolSession : IAsyncDisposable
{
    ConnectionState State { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<IndustrialVariable> ReadAsync(
        string address,
        CancellationToken cancellationToken = default);
    Task WriteAsync(
        string address,
        IndustrialValueType valueType,
        object? value,
        CancellationToken cancellationToken = default);
}

public interface IOpcUaSession : IIndustrialProtocolSession
{
    Task<IReadOnlyList<IndustrialVariable>> ReadManyAsync(
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default);
}

public interface IPlcSession : IIndustrialProtocolSession
{
    Task<bool> ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken = default);
}

public interface IRobotSession : IIndustrialProtocolSession
{
    Task<bool> StartProgramAsync(
        string programName,
        CancellationToken cancellationToken = default);
    Task<bool> StopProgramAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryIndustrialProtocolSession :
    IOpcUaSession,
    IPlcSession,
    IRobotSession
{
    private readonly Dictionary<string, IndustrialVariable> _variables =
        new(StringComparer.OrdinalIgnoreCase);

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = ConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task<IndustrialVariable> ReadAsync(
        string address,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_variables.TryGetValue(address, out var variable)
            ? variable
            : new IndustrialVariable(
                address,
                IndustrialValueType.Bytes,
                Quality: "BadNotFound",
                Timestamp: DateTimeOffset.UtcNow));
    }

    public Task WriteAsync(
        string address,
        IndustrialValueType valueType,
        object? value,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        cancellationToken.ThrowIfCancellationRequested();
        _variables[address] = new IndustrialVariable(
            address,
            valueType,
            value,
            Quality: "Good",
            Timestamp: DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<IndustrialVariable>> ReadManyAsync(
        IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var values = new List<IndustrialVariable>(addresses.Count);
        foreach (var address in addresses)
        {
            values.Add(await ReadAsync(address, cancellationToken));
        }

        return values;
    }

    public Task<bool> ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    public Task<bool> StartProgramAsync(
        string programName,
        CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync($"start:{programName}", cancellationToken);

    public Task<bool> StopProgramAsync(CancellationToken cancellationToken = default) =>
        ExecuteCommandAsync("stop", cancellationToken);

    public ValueTask DisposeAsync()
    {
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("Industrial protocol session is not connected.");
        }
    }
}
