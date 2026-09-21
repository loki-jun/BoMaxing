using System.Text.Json;
using BoMaxing.Core.Devices;

namespace BoMaxing.Core.Project;

public sealed class ProjectDocument
{
    public int SchemaVersion { get; set; } = ProjectSchema.CurrentVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Project";
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<WorkflowDefinition> Workflows { get; set; } = [];
    public List<DeviceConfiguration> Devices { get; set; } = [];
    public List<RecipeDefinition> Recipes { get; set; } = [];
    public HmiDefinition Hmi { get; set; } = new();
    public Dictionary<string, JsonElement> Settings { get; set; } = [];

    public WorkflowDefinition AddWorkflow(string name)
    {
        var workflow = new WorkflowDefinition
        {
            Name = name
        };

        Workflows.Add(workflow);
        Touch();
        return workflow;
    }

    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class HmiDefinition
{
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "BoMaxing HMI";
    public int RefreshIntervalMilliseconds { get; set; } = 500;
    public List<HmiWidgetDefinition> Widgets { get; set; } = [];
}

public enum HmiWidgetKind
{
    Text,
    Number,
    Boolean,
    Status,
    AlarmList,
    Trend,
    Image
}

public sealed class HmiWidgetDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public HmiWidgetKind Kind { get; set; } = HmiWidgetKind.Text;
    public string Title { get; set; } = "Widget";
    public string Binding { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 240;
    public int Height { get; set; } = 120;
    public bool IsVisible { get; set; } = true;
    public Dictionary<string, JsonElement> Settings { get; set; } = [];
}

public sealed class RecipeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Recipe";
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
}

public static class ProjectSchema
{
    public const int CurrentVersion = 4;
}

public sealed class WorkflowDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Workflow";
    public List<WorkflowNodeDefinition> Nodes { get; set; } = [];
    public List<WorkflowEdgeDefinition> Edges { get; set; } = [];

    public WorkflowNodeDefinition AddNode(string toolType, string name)
    {
        var node = new WorkflowNodeDefinition
        {
            ToolType = toolType,
            Name = name
        };

        Nodes.Add(node);
        return node;
    }

    public WorkflowDefinition Connect(
        string fromNodeId,
        string fromPort,
        string toNodeId,
        string toPort)
    {
        Edges.Add(new WorkflowEdgeDefinition
        {
            FromNodeId = fromNodeId,
            FromPort = fromPort,
            ToNodeId = toNodeId,
            ToPort = toPort
        });

        return this;
    }
}

public sealed class WorkflowNodeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Tool";
    public string ToolType { get; set; } = string.Empty;
    public double? PositionX { get; set; }
    public double? PositionY { get; set; }
    public Dictionary<string, JsonElement> Parameters { get; set; } = [];
}

public sealed class WorkflowEdgeDefinition
{
    public string FromNodeId { get; set; } = string.Empty;
    public string FromPort { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public string ToPort { get; set; } = string.Empty;
}
