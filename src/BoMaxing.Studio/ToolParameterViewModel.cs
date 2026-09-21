using System.Text.Json;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Studio;

public sealed class ToolParameterViewModel : ViewModelBase
{
    private string _value;

    public ToolParameterViewModel(
        ToolParameterDefinition definition,
        JsonElement? currentValue = null)
    {
        Definition = definition;
        _value = currentValue?.ToString() ?? definition.DefaultValue ?? string.Empty;
    }

    public ToolParameterDefinition Definition { get; }
    public string Name => Definition.Name;
    public string DisplayName => Definition.DisplayName;
    public ToolParameterType Type => Definition.Type;
    public string Constraints => FormatConstraints(Definition);

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    private static string FormatConstraints(ToolParameterDefinition definition)
    {
        var parts = new List<string>();
        if (definition.Required)
        {
            parts.Add("required");
        }

        if (definition.Minimum.HasValue || definition.Maximum.HasValue)
        {
            parts.Add($"{definition.Minimum?.ToString() ?? "-∞"}..{definition.Maximum?.ToString() ?? "+∞"}");
        }

        return string.Join(" · ", parts);
    }
}
