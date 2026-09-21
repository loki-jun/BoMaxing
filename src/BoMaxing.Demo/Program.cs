using System.Text.Json;
using BoMaxing.Application;
using BoMaxing.Core.Imaging;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("BoMaxing Runtime Demo");
Console.WriteLine("=====================");

var diagnostics = new InMemoryDiagnosticSink();
var runtime = BoMaxingRuntime.CreateDefault(diagnostics);
var project = new ProjectDocument
{
    Name = "Runtime Demo Project"
};
var workflow = project.AddWorkflow("PGM Threshold Measurement");

var source = workflow.AddNode(BuiltInTools.ImageFromPgm, "Image Source");
var threshold = workflow.AddNode(BuiltInTools.GrayThreshold, "Gray Threshold");
threshold.Parameters["minimum"] = JsonSerializer.SerializeToElement(100);
threshold.Parameters["maximum"] = JsonSerializer.SerializeToElement(200);
var measure = workflow.AddNode(BuiltInTools.RegionMeasure, "Region Measure");
measure.Parameters["name"] = JsonSerializer.SerializeToElement("Target");

workflow.Connect(source.Id, "image", threshold.Id, "image");
workflow.Connect(threshold.Id, "region", measure.Id, "region");

var samplePath = Path.Combine(Path.GetTempPath(), $"bomaxing-demo-{Guid.NewGuid():N}.pgm");
await WriteSampleImageAsync(samplePath);
source.Parameters["path"] = JsonSerializer.SerializeToElement(samplePath);

try
{
    Console.WriteLine($"Project: {project.Name}");
    Console.WriteLine($"Workflow: {workflow.Name}");
    Console.WriteLine($"Source parameters: {source.Parameters.Count}");
    foreach (var parameter in source.Parameters)
    {
        Console.WriteLine($"  - {parameter.Key}: {parameter.Value}");
    }
    Console.WriteLine($"Tools: {runtime.ToolRegistry.DescribeAll().Count}");
    foreach (var descriptor in runtime.ToolRegistry.DescribeAll())
    {
        Console.WriteLine($"  - [{descriptor.Category}] {descriptor.DisplayName} ({descriptor.TypeId})");
    }

    Console.WriteLine();
    Console.WriteLine("Executing workflow...");
    var result = await runtime.Projects.RunWorkflowAsync(project, workflow.Id);
    Console.WriteLine($"Workflow result: {(result.Succeeded ? "SUCCESS" : "FAILED")}");

    foreach (var record in result.Records)
    {
        Console.WriteLine(
            $"  {record.NodeName,-18} " +
            $"{(record.Succeeded ? "OK" : "FAIL"),-5} " +
            $"{record.Duration.TotalMilliseconds,8:F2} ms");
        foreach (var diagnostic in record.Diagnostics)
        {
            Console.WriteLine($"    [{diagnostic.Level}] {diagnostic.Code}: {diagnostic.Message}");
        }
    }

    if (result.Succeeded)
    {
        var area = GetMeasurement(result, measure.Id, "area");
        var centerX = GetMeasurement(result, measure.Id, "centerX");
        var centerY = GetMeasurement(result, measure.Id, "centerY");
        Console.WriteLine();
        Console.WriteLine($"Area:   {area.Value:F2} {area.Unit}");
        Console.WriteLine($"Center: ({centerX.Value:F2}, {centerY.Value:F2}) {centerX.Unit}");
    }

    Console.WriteLine($"Diagnostics: {diagnostics.Events.Count}");
}
finally
{
    File.Delete(samplePath);
}

static Measurement GetMeasurement(
    WorkflowExecutionResult result,
    string nodeId,
    string portName)
{
    var value = result.Outputs[$"{nodeId}.{portName}"];
    return value.Value as Measurement
        ?? throw new InvalidDataException(
            $"Expected measurement output '{nodeId}.{portName}'.");
}

static async Task WriteSampleImageAsync(string path)
{
    const string header = "P2\n# BoMaxing demo image\n8 6\n255\n";
    var pixels = new[]
    {
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 120, 140, 160, 180, 0, 0, 0,
        0, 120, 140, 160, 180, 0, 0, 0,
        0, 120, 140, 160, 180, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    };
    var content = header + string.Join(' ', pixels) + Environment.NewLine;
    await File.WriteAllTextAsync(path, content);
}
