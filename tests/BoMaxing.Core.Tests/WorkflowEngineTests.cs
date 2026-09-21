using System.Text.Json;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Core.Tests;

public sealed class WorkflowEngineTests
{
    [Fact]
    public void Alarm_manager_requires_acknowledgement_before_clearing()
    {
        var alarms = new AlarmManager();
        alarms.Raise(new AlarmDefinition(
            "CAMERA_OFFLINE",
            "Camera is offline",
            AlarmSeverity.Error,
            RequiresAcknowledgement: true));

        Assert.Throws<InvalidOperationException>(() => alarms.Clear("CAMERA_OFFLINE"));
        alarms.Acknowledge("CAMERA_OFFLINE");
        Assert.Equal(AlarmState.Cleared, alarms.Clear("CAMERA_OFFLINE").State);
    }

    [Fact]
    public void Audit_trail_preserves_actor_target_and_details()
    {
        var audit = new AuditTrail();
        audit.Append(
            "workflow.run",
            "operator",
            "inspection-1",
            new Dictionary<string, string> { ["recipe"] = "Variant A" });

        var entry = Assert.Single(audit.Snapshot());
        Assert.Equal("operator", entry.Actor);
        Assert.Equal("Variant A", entry.Details!["recipe"]);
    }

    [Fact]
    public void Access_control_authenticates_users_and_enforces_role_permissions()
    {
        var audit = new AuditTrail();
        var access = new AccessControlService(audit);
        access.AddUser("operator", "operator-password", UserRole.Operator);

        Assert.True(access.Authenticate("operator", "operator-password", out var session));
        Assert.NotNull(session);
        Assert.True(access.Can(session!, Permission.RunWorkflow));
        Assert.False(access.Can(session!, Permission.EditDevices));
        Assert.Throws<UnauthorizedAccessException>(() =>
            access.Demand(session!, Permission.EditDevices));
        Assert.False(access.Authenticate("operator", "wrong-password", out _));
        Assert.Contains(audit.Snapshot(), item => item.Action == "permission.denied");
    }
    [Fact]
    public async Task Executes_nodes_in_dependency_order()
    {
        var workflow = new WorkflowDefinition { Name = "Addition" };
        var first = workflow.AddNode(BuiltInTools.Constant, "First");
        first.Parameters["value"] = JsonSerializer.SerializeToElement(12);
        var second = workflow.AddNode(BuiltInTools.Constant, "Second");
        second.Parameters["value"] = JsonSerializer.SerializeToElement(30);
        var add = workflow.AddNode(BuiltInTools.AddNumbers, "Add");

        workflow
            .Connect(first.Id, "value", add.Id, "a")
            .Connect(second.Id, "value", add.Id, "b");

        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());
        var result = await engine.ExecuteAsync(workflow);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Records.Count);
        Assert.Equal(42d, Assert.IsType<double>(result.Outputs[$"{add.Id}.sum"].Value));
    }

    [Fact]
    public async Task Stops_when_a_tool_fails()
    {
        var workflow = new WorkflowDefinition();
        var passThrough = workflow.AddNode(BuiltInTools.PassThrough, "Missing Input");
        var next = workflow.AddNode(BuiltInTools.PassThrough, "Should Not Run");
        workflow.Connect(passThrough.Id, "out", next.Id, "in");

        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());
        var result = await engine.ExecuteAsync(workflow);

        Assert.False(result.Succeeded);
        Assert.Single(result.Records);
        Assert.Equal("INPUT_MISSING", result.Records[0].Diagnostics[0].Code);
    }

    [Fact]
    public async Task Rejects_cycles_before_execution()
    {
        var workflow = new WorkflowDefinition();
        var first = workflow.AddNode(BuiltInTools.PassThrough, "First");
        var second = workflow.AddNode(BuiltInTools.PassThrough, "Second");
        workflow
            .Connect(first.Id, "out", second.Id, "in")
            .Connect(second.Id, "out", first.Id, "in");

        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());

        await Assert.ThrowsAsync<InvalidDataException>(() => engine.ExecuteAsync(workflow));
    }

    [Fact]
    public async Task Rejects_unregistered_tools_before_execution()
    {
        var workflow = new WorkflowDefinition();
        workflow.AddNode("plugin.missing", "Missing Tool");
        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.ExecuteAsync(workflow));

        Assert.Contains("not registered", exception.Message);
    }

    [Fact]
    public async Task Rejects_edges_with_incompatible_port_types()
    {
        var workflow = new WorkflowDefinition();
        var source = workflow.AddNode(BuiltInTools.ImageFromPgm, "Image");
        var target = workflow.AddNode(BuiltInTools.AddNumbers, "Add");
        workflow.Connect(source.Id, "image", target.Id, "a");
        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => engine.ExecuteAsync(workflow));

        Assert.Contains("incompatible data types", exception.Message);
    }

    [Fact]
    public async Task Rejects_invalid_tool_parameters_before_tool_execution()
    {
        var workflow = new WorkflowDefinition();
        var threshold = workflow.AddNode(BuiltInTools.GrayThreshold, "Threshold");
        threshold.Parameters["minimum"] = JsonSerializer.SerializeToElement(300);
        threshold.Parameters["maximum"] = JsonSerializer.SerializeToElement(100);
        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());

        var result = await engine.ExecuteAsync(workflow);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Records[0].Diagnostics,
            diagnostic => diagnostic.Code == "PARAMETER_OUT_OF_RANGE");
    }

    [Fact]
    public async Task Executes_independent_nodes_in_parallel_when_configured()
    {
        var workflow = new WorkflowDefinition();
        var first = workflow.AddNode(BuiltInTools.Constant, "First");
        first.Parameters["value"] = JsonSerializer.SerializeToElement(1);
        var second = workflow.AddNode(BuiltInTools.Constant, "Second");
        second.Parameters["value"] = JsonSerializer.SerializeToElement(2);
        var engine = new WorkflowEngine(BuiltInTools.CreateRegistry());

        var result = await engine.ExecuteAsync(
            workflow,
            options: new WorkflowExecutionOptions { MaxDegreeOfParallelism = 2 });

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public async Task Converts_node_timeout_to_diagnostic_failure()
    {
        var registry = new ToolRegistry();
        registry.Register("test.delay", static () => new DelayTool());
        var workflow = new WorkflowDefinition();
        workflow.AddNode("test.delay", "Slow Tool");
        var engine = new WorkflowEngine(registry);

        var result = await engine.ExecuteAsync(
            workflow,
            options: new WorkflowExecutionOptions
            {
                NodeTimeout = TimeSpan.FromMilliseconds(10)
            });

        Assert.False(result.Succeeded);
        Assert.Equal("TOOL_TIMEOUT", result.Records[0].Diagnostics[0].Code);
    }

    [Fact]
    public async Task Cancellation_is_propagated_to_tools()
    {
        var registry = new ToolRegistry();
        registry.Register("test.delay", static () => new DelayTool());
        var workflow = new WorkflowDefinition();
        workflow.AddNode("test.delay", "Slow Tool");
        var engine = new WorkflowEngine(registry);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ExecuteAsync(workflow, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Run_result_can_be_saved_and_loaded_as_snapshot()
    {
        var workflow = new WorkflowDefinition();
        var node = workflow.AddNode(BuiltInTools.Constant, "Value");
        node.Parameters["value"] = JsonSerializer.SerializeToElement(42);
        var context = new WorkflowRunContext(
            Guid.NewGuid(),
            WorkflowRunMode.Replay,
            Metadata: new Dictionary<string, string> { ["source"] = "test" });
        var result = await new WorkflowEngine(BuiltInTools.CreateRegistry()).ExecuteAsync(
            workflow,
            options: new WorkflowExecutionOptions { RunContext = context });
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.run.json");

        try
        {
            var store = new WorkflowRunSnapshotStore();
            await store.SaveAsync(result.ToSnapshot(context), path);
            var loaded = await store.LoadAsync(path);

            Assert.Equal(context.RunId, loaded.RunId);
            Assert.Equal(WorkflowRunMode.Replay, loaded.Mode);
            Assert.True(loaded.Succeeded);
            Assert.Equal("test", loaded.Metadata["source"]);
            Assert.Single(loaded.Nodes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class DelayTool : ITool
    {
        public string TypeId => "test.delay";
        public ToolDescriptor Descriptor => new(TypeId, "Delay", "Test", [], [], []);

        public async Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return ToolExecutionResult.Success();
        }
    }
}
