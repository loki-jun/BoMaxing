namespace BoMaxing.Core.Runtime;

public enum DiagnosticLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public sealed record DiagnosticEvent(
    DiagnosticLevel Level,
    string Code,
    string Message,
    string? NodeId = null,
    Exception? Exception = null);

public interface IDiagnosticSink
{
    void Write(DiagnosticEvent diagnostic);
}

public sealed class InMemoryDiagnosticSink : IDiagnosticSink
{
    private readonly List<DiagnosticEvent> _events = [];
    private readonly object _lock = new();

    public IReadOnlyList<DiagnosticEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToArray();
            }
        }
    }

    public void Write(DiagnosticEvent diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        lock (_lock)
        {
            _events.Add(diagnostic);
        }
    }
}
