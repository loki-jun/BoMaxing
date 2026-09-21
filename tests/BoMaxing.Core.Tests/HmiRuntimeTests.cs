using BoMaxing.Core.Imaging;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Core.Tests;

public sealed class HmiRuntimeTests
{
    [Fact]
    public void Catalog_finds_known_points_and_rejects_unknown_points()
    {
        Assert.True(HmiDataPointCatalog.TryGet(
            " WORKFLOW.LAST.AREA ",
            out var descriptor));
        Assert.Equal("workflow.last.area", descriptor.Binding);
        Assert.Equal(HmiDataPointType.Number, descriptor.Type);
        Assert.False(HmiDataPointCatalog.TryGet("workflow.missing", out _));
    }

    [Fact]
    public void Catalog_validates_widget_compatibility()
    {
        var valid = HmiDataPointCatalog.Validate(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.Number,
            Binding = "workflow.last.area"
        });
        var invalid = HmiDataPointCatalog.Validate(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.Trend,
            Binding = "workflow.last.area"
        });

        Assert.True(valid.IsValid);
        Assert.Equal("workflow.last.area", valid.Descriptor!.Binding);
        Assert.False(invalid.IsValid);
        Assert.Contains("cannot display", invalid.Message);
    }

    [Fact]
    public void Resolver_reports_unknown_binding()
    {
        var resolved = HmiBindingResolver.Resolve(
            "workflow.unknown",
            new HmiRuntimeState());

        Assert.False(resolved.IsValid);
        Assert.Null(resolved.Value);
        Assert.Contains("Unknown HMI binding", resolved.Message);
    }

    [Fact]
    public void Resolver_reads_measurements_alarm_list_and_trend()
    {
        var alarm = new AlarmEvent(
            new AlarmDefinition("E001", "Test alarm", AlarmSeverity.Error),
            AlarmState.Active,
            DateTimeOffset.UtcNow);
        var history = new[]
        {
            new WorkflowRunHistoryEntry(
                DateTimeOffset.UtcNow,
                "workflow",
                "SUCCESS",
                12.5,
                "ok")
        };
        var trend = new[]
        {
            new WorkflowRunTrendPoint(DateTimeOffset.UtcNow, 1, 1, 12.5)
        };
        var state = new HmiRuntimeState
        {
            LastArea = 42.5,
            ActiveAlarms = [alarm],
            History = history,
            DurationTrend = trend
        };

        var area = HmiBindingResolver.Resolve("workflow.last.area", state);
        var alarms = HmiBindingResolver.Resolve("alarms.active", state);
        var duration = HmiBindingResolver.Resolve("history.duration", state);

        Assert.True(area.IsValid);
        Assert.Equal(42.5, area.Value);
        Assert.Equal("42.5", area.DisplayValue);
        Assert.True(alarms.IsValid);
        Assert.Equal("1 active alarm(s)", alarms.DisplayValue);
        Assert.True(duration.IsValid);
        Assert.Equal(trend, duration.Value);
    }

    [Fact]
    public void Resolver_exposes_image_and_marks_empty_measurement_as_unavailable()
    {
        var image = Image2D.From8Bit(2, 2, [0, 1, 2, 3]);
        var imageValue = HmiBindingResolver.Resolve(
            "workflow.last.image",
            new HmiRuntimeState { LastImage = image });
        var areaValue = HmiBindingResolver.Resolve(
            "workflow.last.area",
            new HmiRuntimeState());

        Assert.True(imageValue.IsValid);
        Assert.Same(image, imageValue.Value);
        Assert.Equal("2 × 2", imageValue.DisplayValue);
        Assert.False(areaValue.IsValid);
        Assert.Equal("--", areaValue.DisplayValue);
        Assert.Equal("No value yet", areaValue.Message);
    }

    [Fact]
    public void Resolver_reports_empty_history_values_without_failing()
    {
        var successRate = HmiBindingResolver.Resolve(
            "history.successRate",
            new HmiRuntimeState());
        var averageDuration = HmiBindingResolver.Resolve(
            "history.averageDurationMilliseconds",
            new HmiRuntimeState());
        var trend = HmiBindingResolver.Resolve(
            "history.duration",
            new HmiRuntimeState());

        Assert.False(successRate.IsValid);
        Assert.False(averageDuration.IsValid);
        Assert.True(trend.IsValid);
        Assert.Equal("--", successRate.DisplayValue);
        Assert.Equal("No trend samples", trend.DisplayValue);
    }
}
