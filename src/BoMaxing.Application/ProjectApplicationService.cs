using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;
using BoMaxing.Core.Devices;

namespace BoMaxing.Application;

public sealed class ProjectApplicationService
{
    private readonly ProjectFileStore _projectFileStore;
    private readonly WorkflowEngine _workflowEngine;
    private readonly DeviceSessionManager? _deviceSessions;
    private readonly DeploymentPackageVerifier _deploymentVerifier = new();
    private readonly DeploymentPackageInstaller _deploymentInstaller = new();

    public ProjectApplicationService(
        ProjectFileStore projectFileStore,
        WorkflowEngine workflowEngine,
        DeviceSessionManager? deviceSessions = null)
    {
        _projectFileStore = projectFileStore;
        _workflowEngine = workflowEngine;
        _deviceSessions = deviceSessions;
    }

    public Task SaveProjectAsync(
        ProjectDocument project,
        string filePath,
        CancellationToken cancellationToken = default) =>
        _projectFileStore.SaveAsync(project, filePath, cancellationToken);

    public Task<ProjectDocument> LoadProjectAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        _projectFileStore.LoadAsync(filePath, cancellationToken);

    public Task<DeploymentVerificationResult> VerifyDeploymentAsync(
        string packagePath,
        CancellationToken cancellationToken = default) =>
        _deploymentVerifier.VerifyAsync(packagePath, cancellationToken);

    public Task<DeploymentState> InstallDeploymentAsync(
        string packagePath,
        string installRoot,
        CancellationToken cancellationToken = default) =>
        _deploymentInstaller.InstallAsync(packagePath, installRoot, cancellationToken);

    public Task<DeploymentState> RollbackDeploymentAsync(
        string installRoot,
        CancellationToken cancellationToken = default) =>
        _deploymentInstaller.RollbackAsync(installRoot, cancellationToken);

    public Task<string> GetCurrentDeploymentPathAsync(
        string installRoot,
        CancellationToken cancellationToken = default) =>
        _deploymentInstaller.GetCurrentReleasePathAsync(installRoot, cancellationToken);

    public Task<DeploymentHealthReport> CheckDeploymentHealthAsync(
        string installRoot,
        CancellationToken cancellationToken = default) =>
        _deploymentInstaller.CheckHealthAsync(installRoot, cancellationToken);

    public Task<int> CleanupDeploymentReleasesAsync(
        string installRoot,
        int keepReleases = 2,
        CancellationToken cancellationToken = default) =>
        _deploymentInstaller.CleanupReleasesAsync(
            installRoot,
            keepReleases,
            cancellationToken);

    public Task<WorkflowExecutionResult> RunWorkflowAsync(
        ProjectDocument project,
        string workflowId,
        IReadOnlyDictionary<string, DataValue>? initialInputs = null,
        WorkflowExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var workflow = project.Workflows.FirstOrDefault(item => item.Id == workflowId)
            ?? throw new KeyNotFoundException($"Workflow '{workflowId}' was not found.");

        _deviceSessions?.Configure(project.Devices);

        return _workflowEngine.ExecuteAsync(
            workflow,
            initialInputs,
            options,
            cancellationToken: cancellationToken);
    }
}
