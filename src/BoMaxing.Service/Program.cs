using System.Text.Json;
using BoMaxing.Application;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

var options = ServiceOptions.Parse(args);
var installRoot = options.InstallRoot ??
                  Environment.GetEnvironmentVariable("BOMAXING_INSTALL_ROOT") ??
                  AppContext.BaseDirectory;
var deployment = new DeploymentPackageInstaller();
if (options.Health)
{
    var health = await deployment.CheckHealthAsync(installRoot);
    Console.WriteLine(
        health.IsHealthy
            ? $"Deployment healthy: {health.ProjectName} / {health.CurrentRelease}"
            : $"Deployment unhealthy: {string.Join("; ", health.Issues)}");
    return health.IsHealthy ? 0 : 1;
}

if (options.Prune)
{
    var removed = await deployment.CleanupReleasesAsync(installRoot);
    Console.WriteLine($"Removed {removed} obsolete deployment release(s).");
    if (!options.RunOnce)
    {
        return 0;
    }
}

var releasePath = await deployment.GetCurrentReleasePathAsync(installRoot);
var projectPath = Path.Combine(releasePath, "project.bomaxing.json");
var project = await new ProjectFileStore().LoadAsync(projectPath);
var workflow = project.Workflows.FirstOrDefault()
    ?? throw new InvalidDataException("The deployed project has no workflow.");
var runtime = BoMaxingRuntime.CreateDefault(new InMemoryDiagnosticSink());
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

Console.WriteLine($"BoMaxing service started: {project.Name} / {workflow.Name}");
try
{
    if (options.RunOnce)
    {
        var result = await runtime.Projects.RunWorkflowAsync(
            project,
            workflow.Id,
            cancellationToken: stopping.Token);
        Console.WriteLine(result.Succeeded ? "Workflow completed successfully." : "Workflow failed.");
        return result.Succeeded ? 0 : 1;
    }

    await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
    Console.WriteLine("BoMaxing service stopped.");
}
finally
{
    await runtime.DeviceSessions.DisposeAsync();
}

return 0;

internal sealed record ServiceOptions(
    string? InstallRoot,
    bool RunOnce,
    bool Health,
    bool Prune)
{
    public static ServiceOptions Parse(string[] args)
    {
        string? installRoot = null;
        var runOnce = false;
        var health = false;
        var prune = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--install-root" when index + 1 < args.Length:
                    installRoot = args[++index];
                    break;
                case "--once":
                    runOnce = true;
                    break;
                case "--health":
                    health = true;
                    break;
                case "--prune":
                    prune = true;
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(
                        "BoMaxing.Service --install-root <path> " +
                        "[--once] [--health] [--prune]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown service argument '{args[index]}'.");
            }
        }

        return new ServiceOptions(installRoot, runOnce, health, prune);
    }
}
