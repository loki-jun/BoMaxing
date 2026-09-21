namespace BoMaxing.Core.Runtime;

public enum ToolParameterType
{
    Number,
    Text,
    Boolean,
    Enum,
    FilePath,
    Json
}

public sealed record ToolPortDefinition(
    string Name,
    DataType DataType,
    bool Required = true);

public sealed record ToolParameterDefinition(
    string Name,
    string DisplayName,
    ToolParameterType Type,
    bool Required = false,
    double? Minimum = null,
    double? Maximum = null,
    string? DefaultValue = null);

public sealed record ToolDescriptor(
    string TypeId,
    string DisplayName,
    string Category,
    IReadOnlyList<ToolPortDefinition> Inputs,
    IReadOnlyList<ToolPortDefinition> Outputs,
    IReadOnlyList<ToolParameterDefinition> Parameters);
