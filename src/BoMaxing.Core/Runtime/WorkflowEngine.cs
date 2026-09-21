using System.Diagnostics;
using BoMaxing.Core.Project;

namespace BoMaxing.Core.Runtime;

public sealed class WorkflowExecutionOptions
{
    public bool StopOnFailure { get; init; } = true;
    public TimeSpan? NodeTimeout { get; init; }
    public int MaxDegreeOfParallelism { get; init; } = 1;
    public WorkflowRunContext? RunContext { get; init; }
}

public sealed class WorkflowExecutionResult
{
    internal WorkflowExecutionResult(
        bool succeeded,
        IReadOnlyList<NodeExecutionRecord> records,
        IReadOnlyDictionary<string, DataValue> outputs)
    {
        Succeeded = succeeded;
        Records = records;
        Outputs = outputs;
    }

    public bool Succeeded { get; }
    public IReadOnlyList<NodeExecutionRecord> Records { get; }
    public IReadOnlyDictionary<string, DataValue> Outputs { get; }

    public WorkflowRunSnapshot ToSnapshot(WorkflowRunContext context) =>
        WorkflowRunSnapshot.FromResult(context, this);
}

public sealed record NodeExecutionRecord(
    string NodeId,
    string NodeName,
    string ToolType,
    bool Succeeded,
    TimeSpan Duration,
    IReadOnlyDictionary<string, DataValue> Outputs,
    IReadOnlyList<DiagnosticEvent> Diagnostics);

public sealed class WorkflowEngine
{
    private readonly ToolRegistry _toolRegistry;
    private readonly IDiagnosticSink _diagnostics;
    private readonly IReadOnlyDictionary<Type, object> _services;

    public WorkflowEngine(
        ToolRegistry toolRegistry,
        IDiagnosticSink? diagnostics = null,
        IReadOnlyDictionary<Type, object>? services = null)
    {
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _diagnostics = diagnostics ?? new InMemoryDiagnosticSink();
        _services = services ?? new Dictionary<Type, object>();
    }

    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, DataValue>? initialInputs = null,
        WorkflowExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var executionOptions = options ?? new WorkflowExecutionOptions();
        if (executionOptions.NodeTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Node timeout must be positive.");
        }

        if (executionOptions.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Maximum degree of parallelism must be positive.");
        }

        var nodes = workflow.Nodes.ToDictionary(node => node.Id);
        ValidateWorkflow(workflow, nodes);
        var orderedNodes = TopologicalSort(workflow, nodes);
        var results = new Dictionary<string, IReadOnlyDictionary<string, DataValue>>();
        var records = new List<NodeExecutionRecord>();
        var inputs = initialInputs ?? new Dictionary<string, DataValue>();
        var runContext = executionOptions.RunContext ?? new WorkflowRunContext(Guid.NewGuid());

        if (executionOptions.MaxDegreeOfParallelism == 1)
        {
            foreach (var node in orderedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await ExecuteNodeAsync(
                    node,
                    workflow.Edges,
                    results,
                    inputs,
                    executionOptions.NodeTimeout,
                    runContext,
                    cancellationToken);
                records.Add(record);
                results[node.Id] = record.Outputs;
                if (!record.Succeeded && executionOptions.StopOnFailure)
                {
                    return new WorkflowExecutionResult(false, records, FlattenOutputs(results));
                }
            }
        }
        else
        {
            await ExecuteParallelAsync(
                workflow,
                orderedNodes,
                results,
                records,
                inputs,
                executionOptions,
                runContext,
                cancellationToken);
            if (records.Any(record => !record.Succeeded))
            {
                return new WorkflowExecutionResult(false, records, FlattenOutputs(results));
            }
        }

        return new WorkflowExecutionResult(true, records, FlattenOutputs(results));
    }

    private async Task ExecuteParallelAsync(
        WorkflowDefinition workflow,
        IReadOnlyList<WorkflowNodeDefinition> orderedNodes,
        Dictionary<string, IReadOnlyDictionary<string, DataValue>> results,
        List<NodeExecutionRecord> records,
        IReadOnlyDictionary<string, DataValue> initialInputs,
        WorkflowExecutionOptions options,
        WorkflowRunContext runContext,
        CancellationToken cancellationToken)
    {
        var nodes = orderedNodes.ToDictionary(node => node.Id);
        var predecessors = nodes.Keys.ToDictionary(
            nodeId => nodeId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        foreach (var edge in workflow.Edges)
        {
            predecessors[edge.ToNodeId].Add(edge.FromNodeId);
        }

        var remaining = new HashSet<string>(nodes.Keys, StringComparer.OrdinalIgnoreCase);
        while (remaining.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = remaining
                .Where(nodeId => predecessors[nodeId].All(results.ContainsKey))
                .Select(nodeId => nodes[nodeId])
                .ToArray();
            if (ready.Length == 0)
            {
                throw new InvalidDataException("Workflow dependencies could not be resolved.");
            }

            var batches = ready.Chunk(options.MaxDegreeOfParallelism);
            foreach (var batch in batches)
            {
                var tasks = batch.Select(node => ExecuteNodeAsync(
                    node,
                    workflow.Edges,
                    results,
                    initialInputs,
                    options.NodeTimeout,
                    runContext,
                    cancellationToken));
                var batchRecords = await Task.WhenAll(tasks);
                foreach (var record in batchRecords)
                {
                    records.Add(record);
                    results[record.NodeId] = record.Outputs;
                    remaining.Remove(record.NodeId);
                }

                if (options.StopOnFailure && batchRecords.Any(record => !record.Succeeded))
                {
                    return;
                }
            }
        }
    }

    private async Task<NodeExecutionRecord> ExecuteNodeAsync(
        WorkflowNodeDefinition node,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DataValue>> results,
        IReadOnlyDictionary<string, DataValue> initialInputs,
        TimeSpan? timeout,
        WorkflowRunContext runContext,
        CancellationToken cancellationToken)
    {
        var nodeInputs = ResolveInputs(node, edges, results, initialInputs);
        var tool = _toolRegistry.Create(node.ToolType);
        var parameterDiagnostics = ToolParameterValidator.Validate(
            tool.Descriptor,
            node.Parameters,
            node.Id);
        if (parameterDiagnostics.Count > 0)
        {
            foreach (var diagnostic in parameterDiagnostics)
            {
                _diagnostics.Write(diagnostic);
            }

            return new NodeExecutionRecord(
                node.Id,
                node.Name,
                node.ToolType,
                false,
                TimeSpan.Zero,
                new Dictionary<string, DataValue>(),
                parameterDiagnostics);
        }

        var context = new ToolExecutionContext(
            node.Id,
            node.Name,
            nodeInputs,
            node.Parameters,
            _diagnostics,
            _services,
            runContext);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue)
        {
            timeoutSource.CancelAfter(timeout.Value);
        }

        var startedAt = Stopwatch.GetTimestamp();
        ToolExecutionResult toolResult;
        try
        {
            toolResult = await tool.ExecuteAsync(context, timeoutSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            var diagnostic = new DiagnosticEvent(
                DiagnosticLevel.Error,
                "TOOL_TIMEOUT",
                $"Tool '{node.Name}' exceeded its execution timeout.",
                node.Id,
                exception);
            _diagnostics.Write(diagnostic);
            toolResult = ToolExecutionResult.Failure(diagnostic);
        }
        catch (Exception exception)
        {
            var diagnostic = new DiagnosticEvent(
                DiagnosticLevel.Error,
                "TOOL_EXECUTION_EXCEPTION",
                $"Tool '{node.Name}' threw an exception.",
                node.Id,
                exception);
            _diagnostics.Write(diagnostic);
            toolResult = ToolExecutionResult.Failure(diagnostic);
        }

        foreach (var diagnostic in toolResult.Diagnostics)
        {
            _diagnostics.Write(diagnostic with { NodeId = node.Id });
        }

        return new NodeExecutionRecord(
            node.Id,
            node.Name,
            node.ToolType,
            toolResult.Succeeded,
            Stopwatch.GetElapsedTime(startedAt),
            toolResult.Outputs,
            toolResult.Diagnostics);
    }

    private static IReadOnlyDictionary<string, DataValue> ResolveInputs(
        WorkflowNodeDefinition node,
        IReadOnlyList<WorkflowEdgeDefinition> edges,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DataValue>> results,
        IReadOnlyDictionary<string, DataValue> initialInputs)
    {
        var inputs = new Dictionary<string, DataValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges.Where(edge => edge.ToNodeId == node.Id))
        {
            if (results.TryGetValue(edge.FromNodeId, out var sourceOutputs) &&
                sourceOutputs.TryGetValue(edge.FromPort, out var value))
            {
                inputs[edge.ToPort] = value;
            }
        }

        foreach (var input in initialInputs)
        {
            var separator = input.Key.IndexOf('.');
            if (separator < 0)
            {
                continue;
            }

            var targetNodeId = input.Key[..separator];
            var targetPort = input.Key[(separator + 1)..];
            if (targetNodeId.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            {
                inputs[targetPort] = input.Value;
            }
        }

        return inputs;
    }

    private static IReadOnlyDictionary<string, DataValue> FlattenOutputs(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, DataValue>> results)
    {
        var outputs = new Dictionary<string, DataValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in results)
        {
            foreach (var output in node.Value)
            {
                outputs[$"{node.Key}.{output.Key}"] = output.Value;
            }
        }

        return outputs;
    }

    private void ValidateWorkflow(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodes)
    {
        if (nodes.Count != workflow.Nodes.Count)
        {
            throw new InvalidDataException("Workflow contains duplicate node IDs.");
        }

        foreach (var node in workflow.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.ToolType))
            {
                throw new InvalidDataException($"Node '{node.Name}' has no tool type.");
            }

            if (!_toolRegistry.Contains(node.ToolType))
            {
                throw new InvalidDataException(
                    $"Tool type '{node.ToolType}' used by node '{node.Name}' is not registered.");
            }
        }

        foreach (var edge in workflow.Edges)
        {
            if (!nodes.ContainsKey(edge.FromNodeId) || !nodes.ContainsKey(edge.ToNodeId))
            {
                throw new InvalidDataException("Workflow contains an edge pointing to a missing node.");
            }

            var source = nodes[edge.FromNodeId];
            var target = nodes[edge.ToNodeId];
            var sourceDescriptor = _toolRegistry.Create(source.ToolType).Descriptor;
            var targetDescriptor = _toolRegistry.Create(target.ToolType).Descriptor;
            var sourcePort = sourceDescriptor.Outputs.FirstOrDefault(
                port => port.Name.Equals(edge.FromPort, StringComparison.OrdinalIgnoreCase));
            var targetPort = targetDescriptor.Inputs.FirstOrDefault(
                port => port.Name.Equals(edge.ToPort, StringComparison.OrdinalIgnoreCase));
            if (sourcePort is null || targetPort is null)
            {
                throw new InvalidDataException(
                    $"Workflow edge '{edge.FromPort}' -> '{edge.ToPort}' references an unknown port.");
            }

            if (sourcePort.DataType != DataType.Unknown &&
                targetPort.DataType != DataType.Unknown &&
                sourcePort.DataType != targetPort.DataType)
            {
                throw new InvalidDataException(
                    $"Workflow edge '{edge.FromPort}' -> '{edge.ToPort}' has incompatible data types " +
                    $"'{sourcePort.DataType}' and '{targetPort.DataType}'.");
            }
        }
    }

    private static IReadOnlyList<WorkflowNodeDefinition> TopologicalSort(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodes)
    {
        var incoming = nodes.Keys.ToDictionary(nodeId => nodeId, _ => 0);
        var outgoing = nodes.Keys.ToDictionary(
            nodeId => nodeId,
            _ => new List<string>());

        foreach (var edge in workflow.Edges)
        {
            incoming[edge.ToNodeId]++;
            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
        }

        var queue = new Queue<string>(incoming
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key));
        var ordered = new List<WorkflowNodeDefinition>(nodes.Count);

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            ordered.Add(nodes[nodeId]);

            foreach (var targetNodeId in outgoing[nodeId])
            {
                incoming[targetNodeId]--;
                if (incoming[targetNodeId] == 0)
                {
                    queue.Enqueue(targetNodeId);
                }
            }
        }

        if (ordered.Count != nodes.Count)
        {
            throw new InvalidDataException("Workflow contains a cycle.");
        }

        return ordered;
    }
}
