namespace BoMaxing.Core.Runtime;

public enum AlarmSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum AlarmState
{
    Active,
    Acknowledged,
    Cleared
}

public sealed record AlarmDefinition(
    string Code,
    string Message,
    AlarmSeverity Severity,
    bool RequiresAcknowledgement = false);

public sealed record AlarmEvent(
    AlarmDefinition Definition,
    AlarmState State,
    DateTimeOffset OccurredAt,
    DateTimeOffset? AcknowledgedAt = null,
    DateTimeOffset? ClearedAt = null,
    string? Source = null);

public sealed class AlarmManager
{
    private readonly Dictionary<string, AlarmEvent> _alarms =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event EventHandler<AlarmEvent>? AlarmChanged;

    public IReadOnlyList<AlarmEvent> GetAll()
    {
        lock (_lock)
        {
            return _alarms.Values.OrderByDescending(item => item.OccurredAt).ToArray();
        }
    }

    public AlarmEvent Raise(
        AlarmDefinition definition,
        string? source = null,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var alarm = new AlarmEvent(
            definition,
            AlarmState.Active,
            occurredAt ?? DateTimeOffset.UtcNow,
            Source: source);
        lock (_lock)
        {
            _alarms[definition.Code] = alarm;
        }

        AlarmChanged?.Invoke(this, alarm);
        return alarm;
    }

    public AlarmEvent Acknowledge(string code, DateTimeOffset? acknowledgedAt = null)
    {
        var current = GetRequired(code);
        var updated = current with
        {
            State = AlarmState.Acknowledged,
            AcknowledgedAt = acknowledgedAt ?? DateTimeOffset.UtcNow
        };
        Update(updated);
        return updated;
    }

    public AlarmEvent Clear(string code, DateTimeOffset? clearedAt = null)
    {
        var current = GetRequired(code);
        if (current.Definition.RequiresAcknowledgement &&
            current.State != AlarmState.Acknowledged)
        {
            throw new InvalidOperationException(
                $"Alarm '{code}' must be acknowledged before it can be cleared.");
        }

        var updated = current with
        {
            State = AlarmState.Cleared,
            ClearedAt = clearedAt ?? DateTimeOffset.UtcNow
        };
        Update(updated);
        return updated;
    }

    private AlarmEvent GetRequired(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        lock (_lock)
        {
            return _alarms.TryGetValue(code, out var alarm)
                ? alarm
                : throw new KeyNotFoundException($"Alarm '{code}' is not active.");
        }
    }

    private void Update(AlarmEvent alarm)
    {
        lock (_lock)
        {
            _alarms[alarm.Definition.Code] = alarm;
        }

        AlarmChanged?.Invoke(this, alarm);
    }
}
