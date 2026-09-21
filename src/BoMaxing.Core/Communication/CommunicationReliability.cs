using System.Net.Sockets;

namespace BoMaxing.Core.Communication;

public sealed record CommunicationRetryOptions
{
    public CommunicationRetryOptions(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        double backoffMultiplier = 2.0)
    {
        if (maxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        var resolvedInitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(100);
        var resolvedMaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(2);
        if (resolvedInitialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        }

        if (resolvedMaximumDelay < resolvedInitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
        }

        if (double.IsNaN(backoffMultiplier) ||
            double.IsInfinity(backoffMultiplier) ||
            backoffMultiplier < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(backoffMultiplier));
        }

        MaxAttempts = maxAttempts;
        InitialDelay = resolvedInitialDelay;
        MaximumDelay = resolvedMaximumDelay;
        BackoffMultiplier = backoffMultiplier;
    }

    public int MaxAttempts { get; }
    public TimeSpan InitialDelay { get; }
    public TimeSpan MaximumDelay { get; }
    public double BackoffMultiplier { get; }
}

public sealed record CommunicationRetryAttempt(
    int Attempt,
    int MaxAttempts,
    Exception Exception,
    TimeSpan NextDelay);

public sealed class RetryingIndustrialProtocolSession : IIndustrialProtocolSession
{
    private readonly IIndustrialProtocolSession _inner;
    private readonly CommunicationRetryOptions _options;
    private readonly Func<Exception, bool> _shouldRetry;
    private bool _disposed;

    public RetryingIndustrialProtocolSession(
        IIndustrialProtocolSession inner,
        CommunicationRetryOptions? options = null,
        Func<Exception, bool>? shouldRetry = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? new CommunicationRetryOptions();
        _shouldRetry = shouldRetry ?? IsTransient;
    }

    public ConnectionState State => _inner.State;

    public event EventHandler<CommunicationRetryAttempt>? RetryAttempted;

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            static (session, token) => session.ConnectAsync(token),
            cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _inner.DisconnectAsync(cancellationToken);

    public Task<IndustrialVariable> ReadAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            (session, token) => session.ReadAsync(address, token),
            cancellationToken);

    public Task WriteAsync(
        string address,
        IndustrialValueType valueType,
        object? value,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            (session, token) => session.WriteAsync(address, valueType, value, token),
            cancellationToken);

    internal Task<T> ExecuteOperationAsync<T>(
        Func<IIndustrialProtocolSession, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        ExecuteAsync(operation, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _inner.DisposeAsync();
    }

    private async Task ExecuteAsync(
        Func<IIndustrialProtocolSession, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            async (session, token) =>
            {
                await operation(session, token);
                return true;
            },
            cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        Func<IIndustrialProtocolSession, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_inner.State != ConnectionState.Connected)
                {
                    await _inner.ConnectAsync(cancellationToken);
                }

                return await operation(_inner, cancellationToken);
            }
            catch (Exception exception) when (
                attempt < _options.MaxAttempts && _shouldRetry(exception))
            {
                lastException = exception;
                var delay = GetDelay(attempt);
                RetryAttempted?.Invoke(
                    this,
                    new CommunicationRetryAttempt(
                        attempt,
                        _options.MaxAttempts,
                        exception,
                        delay));
                await ReconnectAsync(cancellationToken);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new CommunicationException(
            $"Industrial protocol operation failed after {_options.MaxAttempts} attempts.",
            lastException ?? new IOException("No retry attempt was completed."));
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inner.DisconnectAsync(cancellationToken);
        }
        catch (Exception exception) when (_shouldRetry(exception))
        {
            // A failed disconnect should not hide the next connect attempt.
        }

        await _inner.ConnectAsync(cancellationToken);
    }

    private TimeSpan GetDelay(int attempt)
    {
        var milliseconds = _options.InitialDelay.TotalMilliseconds *
                           Math.Pow(_options.BackoffMultiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(
            Math.Min(milliseconds, _options.MaximumDelay.TotalMilliseconds));
    }

    private static bool IsTransient(Exception exception) =>
        exception is CommunicationException or
            IOException or
            SocketException or
            TimeoutException or
            InvalidOperationException;
}

public sealed class RetryingOpcUaSession : IOpcUaSession
{
    private readonly IOpcUaSession _inner;
    private readonly RetryingIndustrialProtocolSession _base;

    public RetryingOpcUaSession(
        IOpcUaSession inner,
        CommunicationRetryOptions? options = null,
        Func<Exception, bool>? shouldRetry = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _base = new RetryingIndustrialProtocolSession(inner, options, shouldRetry);
    }

    public ConnectionState State => _inner.State;
    public event EventHandler<CommunicationRetryAttempt>? RetryAttempted
    {
        add => _base.RetryAttempted += value;
        remove => _base.RetryAttempted -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _base.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _base.DisconnectAsync(cancellationToken);

    public Task<IndustrialVariable> ReadAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        _base.ReadAsync(address, cancellationToken);

    public Task WriteAsync(
        string address,
        IndustrialValueType valueType,
        object? value,
        CancellationToken cancellationToken = default) =>
        _base.WriteAsync(address, valueType, value, cancellationToken);

    internal Task<T> ExecuteOperationAsync<T>(
        Func<IIndustrialProtocolSession, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        _base.ExecuteOperationAsync(operation, cancellationToken);

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

    public ValueTask DisposeAsync() => _base.DisposeAsync();
}

public sealed class RetryingPlcSession : IPlcSession
{
    private readonly IPlcSession _inner;
    private readonly RetryingIndustrialProtocolSession _base;

    public RetryingPlcSession(
        IPlcSession inner,
        CommunicationRetryOptions? options = null,
        Func<Exception, bool>? shouldRetry = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _base = new RetryingIndustrialProtocolSession(inner, options, shouldRetry);
    }

    public ConnectionState State => _inner.State;
    public event EventHandler<CommunicationRetryAttempt>? RetryAttempted
    {
        add => _base.RetryAttempted += value;
        remove => _base.RetryAttempted -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _base.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _base.DisconnectAsync(cancellationToken);

    public Task<IndustrialVariable> ReadAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        _base.ReadAsync(address, cancellationToken);

    public Task WriteAsync(
        string address,
        IndustrialValueType valueType,
        object? value,
        CancellationToken cancellationToken = default) =>
        _base.WriteAsync(address, valueType, value, cancellationToken);

    public Task<bool> ExecuteCommandAsync(
        string command,
        CancellationToken cancellationToken = default) =>
        _base.ExecuteOperationAsync(
            (_, token) => _inner.ExecuteCommandAsync(command, token),
            cancellationToken);

    public ValueTask DisposeAsync() => _base.DisposeAsync();
}

public sealed class RetryingRobotSession : IRobotSession
{
    private readonly IRobotSession _inner;
    private readonly RetryingIndustrialProtocolSession _base;

    public RetryingRobotSession(
        IRobotSession inner,
        CommunicationRetryOptions? options = null,
        Func<Exception, bool>? shouldRetry = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _base = new RetryingIndustrialProtocolSession(inner, options, shouldRetry);
    }

    public ConnectionState State => _inner.State;
    public event EventHandler<CommunicationRetryAttempt>? RetryAttempted
    {
        add => _base.RetryAttempted += value;
        remove => _base.RetryAttempted -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _base.ConnectAsync(cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _base.DisconnectAsync(cancellationToken);

    public Task<IndustrialVariable> ReadAsync(
        string address,
        CancellationToken cancellationToken = default) =>
        _base.ReadAsync(address, cancellationToken);

    public Task WriteAsync(
        string address,
        IndustrialValueType valueType,
        object? value,
        CancellationToken cancellationToken = default) =>
        _base.WriteAsync(address, valueType, value, cancellationToken);

    public Task<bool> StartProgramAsync(
        string programName,
        CancellationToken cancellationToken = default) =>
        _base.ExecuteOperationAsync(
            (_, token) => _inner.StartProgramAsync(programName, token),
            cancellationToken);

    public Task<bool> StopProgramAsync(CancellationToken cancellationToken = default) =>
        _base.ExecuteOperationAsync(
            (_, token) => _inner.StopProgramAsync(token),
            cancellationToken);

    public ValueTask DisposeAsync() => _base.DisposeAsync();
}
