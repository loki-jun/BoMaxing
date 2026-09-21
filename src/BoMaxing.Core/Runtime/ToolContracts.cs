using System.Text.Json;

namespace BoMaxing.Core.Runtime;

public interface ITool
{
    string TypeId { get; }
    ToolDescriptor Descriptor { get; }

    Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken);
}

public enum WorkflowRunMode
{
    Live,
    Replay
}

public sealed record WorkflowRunContext(
    Guid RunId,
    WorkflowRunMode Mode = WorkflowRunMode.Live,
    DateTimeOffset? RecordedAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> Values { get; } =
        Metadata ?? new Dictionary<string, string>();
}

public sealed class ToolExecutionContext
{
    public ToolExecutionContext(
        string nodeId,
        string nodeName,
        IReadOnlyDictionary<string, DataValue> inputs,
        IReadOnlyDictionary<string, JsonElement> parameters,
        IDiagnosticSink diagnostics,
        IReadOnlyDictionary<Type, object>? services = null,
        WorkflowRunContext? runContext = null)
    {
        NodeId = nodeId;
        NodeName = nodeName;
        Inputs = inputs;
        Parameters = parameters;
        Diagnostics = diagnostics;
        Services = services ?? new Dictionary<Type, object>();
        RunContext = runContext ?? new WorkflowRunContext(Guid.NewGuid());
    }

    public string NodeId { get; }
    public string NodeName { get; }
    public IReadOnlyDictionary<string, DataValue> Inputs { get; }
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; }
    public IDiagnosticSink Diagnostics { get; }
    public IReadOnlyDictionary<Type, object> Services { get; }
    public WorkflowRunContext RunContext { get; }

    public bool TryGetInput(string portName, out DataValue value) =>
        Inputs.TryGetValue(portName, out value!);

    public bool TryGetParameter(string parameterName, out JsonElement value) =>
        Parameters.TryGetValue(parameterName, out value);

    public bool TryGetService<TService>(out TService service)
        where TService : class
    {
        if (Services.TryGetValue(typeof(TService), out var value) && value is TService typed)
        {
            service = typed;
            return true;
        }

        service = null!;
        return false;
    }
}

public sealed class ToolExecutionResult
{
    private ToolExecutionResult(
        bool succeeded,
        IReadOnlyDictionary<string, DataValue> outputs,
        IReadOnlyList<DiagnosticEvent> diagnostics)
    {
        Succeeded = succeeded;
        Outputs = outputs;
        Diagnostics = diagnostics;
    }

    public bool Succeeded { get; }
    public IReadOnlyDictionary<string, DataValue> Outputs { get; }
    public IReadOnlyList<DiagnosticEvent> Diagnostics { get; }

    public static ToolExecutionResult Success(
        IReadOnlyDictionary<string, DataValue>? outputs = null,
        IReadOnlyList<DiagnosticEvent>? diagnostics = null) =>
        new(true, outputs ?? new Dictionary<string, DataValue>(), diagnostics ?? []);

    public static ToolExecutionResult Failure(
        DiagnosticEvent diagnostic,
        IReadOnlyList<DiagnosticEvent>? diagnostics = null)
    {
        var allDiagnostics = diagnostics is null
            ? new[] { diagnostic }
            : diagnostics.Append(diagnostic).ToArray();

        return new(false, new Dictionary<string, DataValue>(), allDiagnostics);
    }
}

public sealed class ToolRegistry
{
    private readonly Dictionary<string, Func<ITool>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string typeId, Func<ITool> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeId);
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(typeId, factory))
        {
            throw new InvalidOperationException($"Tool type '{typeId}' is already registered.");
        }
    }

    public ITool Create(string typeId)
    {
        if (!_factories.TryGetValue(typeId, out var factory))
        {
            throw new KeyNotFoundException($"Tool type '{typeId}' is not registered.");
        }

        return factory();
    }

    public bool Contains(string typeId) => _factories.ContainsKey(typeId);

    public IReadOnlyList<ToolDescriptor> DescribeAll() =>
        _factories.Values
            .Select(factory => factory().Descriptor)
            .OrderBy(descriptor => descriptor.Category)
            .ThenBy(descriptor => descriptor.DisplayName)
            .ToArray();
}
