namespace BoMaxing.Core.Runtime;

public sealed record AuditEvent(
    string Action,
    string Actor,
    DateTimeOffset OccurredAt,
    string? Target = null,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed class AuditTrail
{
    private readonly List<AuditEvent> _events = [];
    private readonly object _lock = new();

    public void Append(
        string action,
        string actor,
        string? target = null,
        IReadOnlyDictionary<string, string>? details = null,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        lock (_lock)
        {
            _events.Add(new AuditEvent(
                action,
                actor,
                occurredAt ?? DateTimeOffset.UtcNow,
                target,
                details));
        }
    }

    public IReadOnlyList<AuditEvent> Snapshot()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }
}
