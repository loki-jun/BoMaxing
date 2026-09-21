using BoMaxing.Core.Imaging;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Core.Project;

public enum HmiDataPointType
{
    Text,
    Number,
    Boolean,
    AlarmList,
    Trend,
    Image
}

public sealed record HmiDataPointDescriptor(
    string Binding,
    string DisplayName,
    HmiDataPointType Type);

public sealed record HmiBindingValidation(
    bool IsValid,
    string Message,
    HmiDataPointDescriptor? Descriptor = null);

public sealed record HmiResolvedValue(
    string Binding,
    HmiDataPointType Type,
    object? Value,
    string DisplayValue,
    bool IsValid,
    string Message);

public sealed record HmiRuntimeState
{
    public string RuntimeStatus { get; init; } = "Ready";
    public bool IsRunning { get; init; }
    public string LastResult { get; init; } = "No run yet";
    public string ResultSummary { get; init; } = string.Empty;
    public double? LastArea { get; init; }
    public double? LastCenterX { get; init; }
    public double? LastCenterY { get; init; }
    public Image2D? LastImage { get; init; }
    public IReadOnlyList<AlarmEvent> ActiveAlarms { get; init; } = [];
    public IReadOnlyList<WorkflowRunHistoryEntry> History { get; init; } = [];
    public IReadOnlyList<WorkflowRunTrendPoint> DurationTrend { get; init; } = [];
}

public static class HmiDataPointCatalog
{
    private static readonly IReadOnlyList<HmiDataPointDescriptor> Points =
    [
        new("runtime.status", "Runtime status", HmiDataPointType.Text),
        new("runtime.isRunning", "Workflow running", HmiDataPointType.Boolean),
        new("workflow.last.status", "Last workflow status", HmiDataPointType.Text),
        new("workflow.last.summary", "Last workflow summary", HmiDataPointType.Text),
        new("workflow.last.area", "Last measured area", HmiDataPointType.Number),
        new("workflow.last.centerX", "Last center X", HmiDataPointType.Number),
        new("workflow.last.centerY", "Last center Y", HmiDataPointType.Number),
        new("workflow.last.image", "Last image", HmiDataPointType.Image),
        new("alarms.activeCount", "Active alarm count", HmiDataPointType.Number),
        new("alarms.active", "Active alarms", HmiDataPointType.AlarmList),
        new("history.runCount", "Run count", HmiDataPointType.Number),
        new("history.successRate", "Success rate", HmiDataPointType.Number),
        new(
            "history.averageDurationMilliseconds",
            "Average duration",
            HmiDataPointType.Number),
        new("history.duration", "Duration trend", HmiDataPointType.Trend)
    ];

    private static readonly IReadOnlyDictionary<string, HmiDataPointDescriptor> ByBinding =
        Points.ToDictionary(
            point => point.Binding,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<HmiDataPointDescriptor> GetAll() => Points;

    public static bool TryGet(
        string binding,
        out HmiDataPointDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            descriptor = null!;
            return false;
        }

        return ByBinding.TryGetValue(binding.Trim(), out descriptor!);
    }

    public static HmiBindingValidation Validate(
        HmiWidgetDefinition widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        if (!TryGet(widget.Binding, out var descriptor))
        {
            return new HmiBindingValidation(
                false,
                $"Unknown HMI binding '{widget.Binding}'.");
        }

        if (!IsCompatible(widget.Kind, descriptor.Type))
        {
            return new HmiBindingValidation(
                false,
                $"Widget kind {widget.Kind} cannot display {descriptor.Type}.",
                descriptor);
        }

        return new HmiBindingValidation(true, "OK", descriptor);
    }

    public static bool IsCompatible(
        HmiWidgetKind widgetKind,
        HmiDataPointType pointType) =>
        widgetKind switch
        {
            HmiWidgetKind.Text => pointType is
                HmiDataPointType.Text or
                HmiDataPointType.Number or
                HmiDataPointType.Boolean,
            HmiWidgetKind.Number => pointType == HmiDataPointType.Number,
            HmiWidgetKind.Boolean => pointType == HmiDataPointType.Boolean,
            HmiWidgetKind.Status => pointType == HmiDataPointType.Text,
            HmiWidgetKind.AlarmList => pointType == HmiDataPointType.AlarmList,
            HmiWidgetKind.Trend => pointType == HmiDataPointType.Trend,
            HmiWidgetKind.Image => pointType == HmiDataPointType.Image,
            _ => false
        };
}

public static class HmiBindingResolver
{
    public static HmiResolvedValue Resolve(
        string binding,
        HmiRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var normalized = binding?.Trim() ?? string.Empty;
        if (!HmiDataPointCatalog.TryGet(normalized, out var descriptor))
        {
            return new HmiResolvedValue(
                normalized,
                HmiDataPointType.Text,
                null,
                "Unknown binding",
                false,
                $"Unknown HMI binding '{binding}'.");
        }

        return normalized.ToLowerInvariant() switch
        {
            "runtime.status" => Text(descriptor, state.RuntimeStatus),
            "runtime.isrunning" => Boolean(descriptor, state.IsRunning),
            "workflow.last.status" => Text(descriptor, state.LastResult),
            "workflow.last.summary" => Text(descriptor, state.ResultSummary),
            "workflow.last.area" => Number(descriptor, state.LastArea),
            "workflow.last.centerx" => Number(descriptor, state.LastCenterX),
            "workflow.last.centery" => Number(descriptor, state.LastCenterY),
            "workflow.last.image" => Image(descriptor, state.LastImage),
            "alarms.activecount" => Number(
                descriptor,
                state.ActiveAlarms.Count(alarm => alarm.State != AlarmState.Cleared)),
            "alarms.active" => AlarmList(
                descriptor,
                state.ActiveAlarms
                    .Where(alarm => alarm.State != AlarmState.Cleared)
                    .ToArray()),
            "history.runcount" => Number(descriptor, state.History.Count),
            "history.successrate" => Number(
                descriptor,
                CalculateSuccessRate(state.History)),
            "history.averagedurationmilliseconds" => Number(
                descriptor,
                CalculateAverageDuration(state.History)),
            "history.duration" => Trend(descriptor, state.DurationTrend),
            _ => new HmiResolvedValue(
                normalized,
                descriptor.Type,
                null,
                "No resolver",
                false,
                $"No resolver is registered for '{binding}'.")
        };
    }

    private static HmiResolvedValue Text(
        HmiDataPointDescriptor descriptor,
        string value) =>
        new(
            descriptor.Binding,
            descriptor.Type,
            value,
            value,
            true,
            "OK");

    private static HmiResolvedValue Boolean(
        HmiDataPointDescriptor descriptor,
        bool value) =>
        new(
            descriptor.Binding,
            descriptor.Type,
            value,
            value ? "True" : "False",
            true,
            "OK");

    private static HmiResolvedValue Number(
        HmiDataPointDescriptor descriptor,
        double? value)
    {
        var display = value.HasValue ? value.Value.ToString("0.###") : "--";
        return new HmiResolvedValue(
            descriptor.Binding,
            descriptor.Type,
            value,
            display,
            value.HasValue,
            value.HasValue ? "OK" : "No value yet");
    }

    private static HmiResolvedValue Image(
        HmiDataPointDescriptor descriptor,
        Image2D? value) =>
        new(
            descriptor.Binding,
            descriptor.Type,
            value,
            value is null ? "No image yet" : $"{value.Width} × {value.Height}",
            value is not null,
            value is null ? "No image yet" : "OK");

    private static HmiResolvedValue AlarmList(
        HmiDataPointDescriptor descriptor,
        IReadOnlyList<AlarmEvent> value) =>
        new(
            descriptor.Binding,
            descriptor.Type,
            value,
            value.Count == 0 ? "No active alarms" : $"{value.Count} active alarm(s)",
            true,
            "OK");

    private static HmiResolvedValue Trend(
        HmiDataPointDescriptor descriptor,
        IReadOnlyList<WorkflowRunTrendPoint> value) =>
        new(
            descriptor.Binding,
            descriptor.Type,
            value,
            value.Count == 0 ? "No trend samples" : $"{value.Count} trend bucket(s)",
            true,
            "OK");

    private static double? CalculateSuccessRate(
        IReadOnlyList<WorkflowRunHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return null;
        }

        var successful = history.Count(entry =>
            string.Equals(entry.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase));
        return successful * 100d / history.Count;
    }

    private static double? CalculateAverageDuration(
        IReadOnlyList<WorkflowRunHistoryEntry> history) =>
        history.Count == 0
            ? null
            : history.Average(entry => entry.DurationMilliseconds);
}
