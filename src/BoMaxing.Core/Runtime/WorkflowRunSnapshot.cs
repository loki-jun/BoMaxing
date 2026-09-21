using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoMaxing.Core.Runtime;

public sealed class WorkflowRunSnapshot
{
    public Guid RunId { get; set; }
    public WorkflowRunMode Mode { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public bool Succeeded { get; set; }
    public List<WorkflowNodeSnapshot> Nodes { get; set; } = [];
    public Dictionary<string, string> Metadata { get; set; } = [];

    public static WorkflowRunSnapshot FromResult(
        WorkflowRunContext context,
        WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);

        return new WorkflowRunSnapshot
        {
            RunId = context.RunId,
            Mode = context.Mode,
            StartedAt = context.StartedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Succeeded = result.Succeeded,
            Metadata = new Dictionary<string, string>(context.Values),
            Nodes = result.Records.Select(WorkflowNodeSnapshot.FromRecord).ToList()
        };
    }
}

public sealed class WorkflowNodeSnapshot
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public string ToolType { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public double DurationMilliseconds { get; set; }
    public List<DiagnosticSnapshot> Diagnostics { get; set; } = [];
    public Dictionary<string, string> OutputTypes { get; set; } = [];

    public static WorkflowNodeSnapshot FromRecord(NodeExecutionRecord record) =>
        new()
        {
            NodeId = record.NodeId,
            NodeName = record.NodeName,
            ToolType = record.ToolType,
            Succeeded = record.Succeeded,
            DurationMilliseconds = record.Duration.TotalMilliseconds,
            Diagnostics = record.Diagnostics.Select(DiagnosticSnapshot.FromEvent).ToList(),
            OutputTypes = record.Outputs.ToDictionary(
                output => output.Key,
                output => output.Value.Type.ToString(),
                StringComparer.OrdinalIgnoreCase)
        };
}

public sealed class DiagnosticSnapshot
{
    public DiagnosticLevel Level { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? NodeId { get; set; }

    public static DiagnosticSnapshot FromEvent(DiagnosticEvent diagnostic) =>
        new()
        {
            Level = diagnostic.Level,
            Code = diagnostic.Code,
            Message = diagnostic.Message,
            NodeId = diagnostic.NodeId
        };
}

public sealed class WorkflowRunSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(
        WorkflowRunSnapshot snapshot,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken);
    }

    public async Task<WorkflowRunSnapshot> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync<WorkflowRunSnapshot>(
                   stream,
                   Options,
                   cancellationToken)
               ?? throw new InvalidDataException("Run snapshot is empty.");
    }
}
