using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;
using BoMaxing.Core.Communication;
using BoMaxing.Core.Devices;
using BoMaxing.Core.Devices.Native;

namespace BoMaxing.Application;

public sealed class BoMaxingRuntime
{
    private BoMaxingRuntime(
        ToolRegistry toolRegistry,
        CommunicationChannelRegistry channels,
        ModbusClientRegistry modbusClients,
        IndustrialProtocolRegistry industrialProtocols,
        DevicePluginRegistry devicePlugins,
        DeviceSessionManager deviceSessions,
        AlarmManager alarms,
        AuditTrail auditTrail,
        AccessControlService accessControl,
        DeploymentPackageVerifier deploymentVerifier,
        DeploymentPackageInstaller deploymentInstaller,
        WorkflowEngine workflowEngine,
        ProjectApplicationService projects)
    {
        ToolRegistry = toolRegistry;
        Channels = channels;
        ModbusClients = modbusClients;
        IndustrialProtocols = industrialProtocols;
        DevicePlugins = devicePlugins;
        DeviceSessions = deviceSessions;
        Alarms = alarms;
        AuditTrail = auditTrail;
        AccessControl = accessControl;
        DeploymentVerifier = deploymentVerifier;
        DeploymentInstaller = deploymentInstaller;
        WorkflowEngine = workflowEngine;
        Projects = projects;
    }

    public ToolRegistry ToolRegistry { get; }
    public CommunicationChannelRegistry Channels { get; }
    public ModbusClientRegistry ModbusClients { get; }
    public IndustrialProtocolRegistry IndustrialProtocols { get; }
    public DevicePluginRegistry DevicePlugins { get; }
    public DeviceSessionManager DeviceSessions { get; }
    public AlarmManager Alarms { get; }
    public AuditTrail AuditTrail { get; }
    public AccessControlService AccessControl { get; }
    public DeploymentPackageVerifier DeploymentVerifier { get; }
    public DeploymentPackageInstaller DeploymentInstaller { get; }
    public WorkflowEngine WorkflowEngine { get; }
    public ProjectApplicationService Projects { get; }

    public static BoMaxingRuntime CreateDefault(IDiagnosticSink? diagnostics = null)
    {
        var channels = new CommunicationChannelRegistry();
        var modbusClients = new ModbusClientRegistry();
        var industrialProtocols = new IndustrialProtocolRegistry();
        var toolRegistry = BuiltInTools.CreateRegistry();
        var devicePlugins = new DevicePluginRegistry();
        devicePlugins.Register(new RecordedCameraPlugin());
        devicePlugins.Register(new NativeDriverCameraPlugin());
        var deviceSessions = new DeviceSessionManager(devicePlugins);
        var alarms = new AlarmManager();
        var auditTrail = new AuditTrail();
        var accessControl = new AccessControlService(auditTrail);
        var bootstrapPassword = Environment.GetEnvironmentVariable("BOMAXING_ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            accessControl.AddUser("admin", bootstrapPassword, UserRole.Administrator);
        }
        var workflowEngine = new WorkflowEngine(
            toolRegistry,
            diagnostics,
            new Dictionary<Type, object>
            {
                [typeof(CommunicationChannelRegistry)] = channels,
                [typeof(ModbusClientRegistry)] = modbusClients,
                [typeof(DeviceSessionManager)] = deviceSessions,
                [typeof(IndustrialProtocolRegistry)] = industrialProtocols
            });
        var projectFileStore = new ProjectFileStore();
        var deploymentVerifier = new DeploymentPackageVerifier();
        var deploymentInstaller = new DeploymentPackageInstaller();
        var projects = new ProjectApplicationService(
            projectFileStore,
            workflowEngine,
            deviceSessions);
        return new BoMaxingRuntime(
            toolRegistry,
            channels,
            modbusClients,
            industrialProtocols,
            devicePlugins,
            deviceSessions,
            alarms,
            auditTrail,
            accessControl,
            deploymentVerifier,
            deploymentInstaller,
            workflowEngine,
            projects);
    }
}
