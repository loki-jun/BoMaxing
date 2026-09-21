using System.Globalization;
using System.Text.Json;

namespace BoMaxing.Core.Runtime;

public static class ToolParameterValidator
{
    public static IReadOnlyList<DiagnosticEvent> Validate(
        ToolDescriptor descriptor,
        IReadOnlyDictionary<string, JsonElement> parameters,
        string? nodeId = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(parameters);

        var diagnostics = new List<DiagnosticEvent>();
        foreach (var definition in descriptor.Parameters)
        {
            if (!parameters.TryGetValue(definition.Name, out var value))
            {
                if (definition.Required && string.IsNullOrWhiteSpace(definition.DefaultValue))
                {
                    diagnostics.Add(new DiagnosticEvent(
                        DiagnosticLevel.Error,
                        "PARAMETER_MISSING",
                        $"Required parameter '{definition.Name}' is missing.",
                        nodeId));
                }

                continue;
            }

            if (!IsTypeValid(definition.Type, value))
            {
                diagnostics.Add(new DiagnosticEvent(
                    DiagnosticLevel.Error,
                    "PARAMETER_INVALID",
                    $"Parameter '{definition.Name}' has an invalid value type.",
                    nodeId));
                continue;
            }

            if (definition.Type == ToolParameterType.Number && value.TryGetDouble(out var number))
            {
                if (definition.Minimum.HasValue && number < definition.Minimum.Value ||
                    definition.Maximum.HasValue && number > definition.Maximum.Value)
                {
                    diagnostics.Add(new DiagnosticEvent(
                        DiagnosticLevel.Error,
                        "PARAMETER_OUT_OF_RANGE",
                        $"Parameter '{definition.Name}' is outside its allowed range.",
                        nodeId));
                }
            }
        }

        return diagnostics;
    }

    private static bool IsTypeValid(ToolParameterType type, JsonElement value) => type switch
    {
        ToolParameterType.Number => value.ValueKind == JsonValueKind.Number &&
                                     value.TryGetDouble(out _),
        ToolParameterType.Text or ToolParameterType.FilePath or ToolParameterType.Enum =>
            value.ValueKind == JsonValueKind.String,
        ToolParameterType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        ToolParameterType.Json => value.ValueKind is not JsonValueKind.Undefined and
                                  not JsonValueKind.Null,
        _ => false
    };
}
