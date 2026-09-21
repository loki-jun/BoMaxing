using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using BoMaxing.Application;
using BoMaxing.Core.Devices;
using BoMaxing.Core.Imaging;
using BoMaxing.Core.Project;
using BoMaxing.Core.Runtime;

namespace BoMaxing.Studio;

public sealed class StudioViewModel : ViewModelBase
{
    private readonly BoMaxingRuntime _runtime;
    private UserSession _session;
    private readonly InMemoryDiagnosticSink _diagnostics;
    private readonly ProjectDocument _project;
    private WorkflowDefinition _workflow;
    private WorkflowNodeDefinition _sourceNode;
    private WorkflowNodeDefinition _thresholdNode;
    private WorkflowNodeDefinition _measureNode;
    private string _projectPath;
    private string _projectName;
    private string _workflowName;
    private string _runtimeStatus = "Ready";
    private string _runtimeStatusColor = "#45D6A7";
    private string _lastResult = "No run yet";
    private string _resultSummary = "Run the workflow to inspect the latest measurements.";
    private bool _isRunning;
    private WorkflowNodeViewModel? _selectedNode;
    private CancellationTokenSource? _runCancellation;
    private readonly Stack<ProjectEdit> _undoHistory = new();
    private readonly Stack<ProjectEdit> _redoHistory = new();
    private PortViewModel? _pendingOutput;
    private ProjectState? _nodeMoveBefore;
    private Bitmap? _previewBitmap;
    private string _previewSummary = "No preview yet";
    private DeviceItemViewModel? _selectedDevice;
    private RecipeItemViewModel? _selectedRecipe;
    private double? _lastArea;
    private double? _lastCenterX;
    private double? _lastCenterY;
    private Image2D? _lastImage;
    private HmiRuntimeState _hmiRuntimeState = new();
    private readonly WorkflowRunHistoryStore _runHistoryStore = new();
    private readonly List<WorkflowRunHistoryEntry> _runHistoryEntries = [];
    private string _runHistoryPath = string.Empty;

    public StudioViewModel()
    {
        _diagnostics = new InMemoryDiagnosticSink();
        _runtime = BoMaxingRuntime.CreateDefault(_diagnostics);
        _session = CreateStudioSession();
        LoginUserName = _session.UserName;
        _projectName = "Runtime Demo Project";
        _workflowName = "PGM Threshold Measurement";

        _project = new ProjectDocument { Name = _projectName };
        _projectPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BoMaxing",
            "studio-project.bomaxing.json");
        _runHistoryPath = GetRunHistoryPath(_project.Id);
        _workflow = _project.AddWorkflow(_workflowName);
        _sourceNode = _workflow.AddNode(BuiltInTools.ImageFromPgm, "Image Source");
        _thresholdNode = _workflow.AddNode(BuiltInTools.GrayThreshold, "Gray Threshold");
        _thresholdNode.Parameters["minimum"] = JsonSerializer.SerializeToElement(100);
        _thresholdNode.Parameters["maximum"] = JsonSerializer.SerializeToElement(200);
        _measureNode = _workflow.AddNode(BuiltInTools.RegionMeasure, "Region Measure");
        _measureNode.Parameters["name"] = JsonSerializer.SerializeToElement("Target");
        _workflow.Connect(_sourceNode.Id, "image", _thresholdNode.Id, "image");
        _workflow.Connect(_thresholdNode.Id, "region", _measureNode.Id, "region");
        EnsureDefaultHmi();

        Tools = new ObservableCollection<ToolItemViewModel>(
            _runtime.ToolRegistry.DescribeAll().Select(descriptor =>
                new ToolItemViewModel(
                    descriptor,
                    AddToolToWorkflow,
                    () => Can(Permission.EditWorkflow))));
        Nodes = new ObservableCollection<WorkflowNodeViewModel>(
            _workflow.Nodes.Select((node, index) => CreateNodeViewModel(node, index)));
        Connections = new ObservableCollection<WorkflowEdgeViewModel>(CreateEdgeViewModels());
        RefreshProjectItems();
        RefreshHmiWidgets();
        SourceNode = Nodes[0];
        ThresholdNode = Nodes[1];
        MeasureNode = Nodes[2];
        Logs = new ObservableCollection<string>();
        Alarms = new ObservableCollection<AlarmItemViewModel>();
        RefreshHmiRuntime();
        RunWorkflowCommand = new AsyncCommand(
            RunWorkflowAsync,
            () => !IsRunning && Can(Permission.RunWorkflow));
        StopWorkflowCommand = new RelayCommand(
            _ => StopWorkflow(),
            () => IsRunning && Can(Permission.RunWorkflow));
        SaveProjectCommand = new AsyncCommand(
            SaveProjectAsync,
            () => !IsRunning && Can(Permission.EditWorkflow));
        OpenProjectCommand = new AsyncCommand(
            OpenProjectAsync,
            () => !IsRunning && Can(Permission.EditWorkflow));
        UndoCommand = new RelayCommand(
            _ => Undo(),
            () => _undoHistory.Count > 0 && !IsRunning && Can(Permission.EditWorkflow));
        RedoCommand = new RelayCommand(
            _ => Redo(),
            () => _redoHistory.Count > 0 && !IsRunning && Can(Permission.EditWorkflow));
        AddRecordedCameraCommand = new RelayCommand(
            _ => AddRecordedCamera(),
            () => Can(Permission.EditDevices));
        AddNativeCameraCommand = new RelayCommand(
            _ => AddNativeCamera(),
            () => Can(Permission.EditDevices));
        AddRecipeCommand = new RelayCommand(
            _ => AddRecipe(),
            () => Can(Permission.EditRecipes));
        RefreshDeviceFeaturesCommand = new AsyncCommand(
            RefreshDeviceFeaturesAsync,
            () => HasSelectedDevice && Can(Permission.EditDevices));
        SetDeviceFeatureCommand = new AsyncCommand<object?>(
            SetDeviceFeatureAsync,
            () => HasSelectedDevice && Can(Permission.EditDevices));
        LoginCommand = new RelayCommand(_ => Login(), () =>
            !string.IsNullOrWhiteSpace(LoginUserName) &&
            !string.IsNullOrWhiteSpace(LoginPassword));
        LogoutCommand = new RelayCommand(_ => Logout(), () => IsAuthenticated);
        AcknowledgeAlarmCommand = new RelayCommand(
            parameter => AcknowledgeAlarm(parameter as AlarmItemViewModel),
            () => !IsRunning && Can(Permission.AcknowledgeAlarm));
        ClearAlarmCommand = new RelayCommand(
            parameter => ClearAlarm(parameter as AlarmItemViewModel),
            () => !IsRunning && Can(Permission.AcknowledgeAlarm));
        AddHmiWidgetCommand = new RelayCommand(
            _ => AddHmiWidget(),
            () => !IsRunning && Can(Permission.EditWorkflow));
        OpenHmiRuntimeCommand = new RelayCommand(
            _ => OpenHmiRuntimeRequested?.Invoke(this, EventArgs.Empty));
        _runtime.Alarms.AlarmChanged += OnAlarmChanged;
        _ = LoadRunHistoryAsync();
    }

    public event EventHandler? OpenHmiRuntimeRequested;

    public string ProjectName => _projectName;
    public string WorkflowName => _workflowName;
    public string CurrentUserName => _session.UserName;
    public string CurrentUserRole => _session.Role.ToString();
    public string CurrentUserSummary => $"{CurrentUserName} · {CurrentUserRole}";
    private string _loginUserName = string.Empty;
    private string _loginPassword = string.Empty;
    public string LoginUserName
    {
        get => _loginUserName;
        set
        {
            if (SetProperty(ref _loginUserName, value))
            {
                (LoginCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    public string LoginPassword
    {
        get => _loginPassword;
        set
        {
            if (SetProperty(ref _loginPassword, value))
            {
                (LoginCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }
    private string _loginStatus = "Local operator session";
    public string LoginStatus
    {
        get => _loginStatus;
        private set => SetProperty(ref _loginStatus, value);
    }
    public bool IsAuthenticated => !CurrentUserName.StartsWith("local-", StringComparison.OrdinalIgnoreCase);
    public int OnlineDeviceCount => Devices.Count(item => item.State == DeviceState.Connected);
    public int ActiveAlarmCount => Alarms.Count(item => item.State != AlarmState.Cleared);
    public string HmiSummary =>
        $"{OnlineDeviceCount} device{(OnlineDeviceCount == 1 ? string.Empty : "s")} online · " +
        $"{ActiveAlarmCount} active alarm{(ActiveAlarmCount == 1 ? string.Empty : "s")}";
    public bool CanRun => Can(Permission.RunWorkflow);
    public bool CanEditWorkflow => Can(Permission.EditWorkflow);
    public bool CanEditDevices => Can(Permission.EditDevices);
    public bool CanEditRecipes => Can(Permission.EditRecipes);
    public bool CanAcknowledgeAlarm => Can(Permission.AcknowledgeAlarm);
    public int RunCount => _runHistoryEntries.Count;
    public int SuccessfulRunCount => _runHistoryEntries.Count(item => item.Status == "SUCCESS");
    public string SuccessRate => RunCount == 0
        ? "--"
        : $"{SuccessfulRunCount * 100d / RunCount:F0}%";
    public string AverageRunDuration => RunCount == 0
        ? "--"
        : $"{_runHistoryEntries.Average(item => item.DurationMilliseconds):F1} ms";
    public string TrendSummary => RunCount == 0
        ? "No trend samples"
        : string.Join(
            "  ",
            _runHistoryEntries
                .Take(8)
                .Reverse()
                .Select(item =>
                    $"{item.Status[..Math.Min(1, item.Status.Length)]}:{item.DurationMilliseconds:F0}"));
    public ObservableCollection<ToolItemViewModel> Tools { get; }
    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];
    public ObservableCollection<RecipeItemViewModel> Recipes { get; } = [];
    public ObservableCollection<DeviceFeatureItemViewModel> DeviceFeatures { get; } = [];
    public ObservableCollection<AlarmItemViewModel> Alarms { get; }
    public ObservableCollection<HmiWidgetItemViewModel> HmiWidgets { get; } = [];
    public ObservableCollection<WorkflowNodeViewModel> Nodes { get; }
    public ObservableCollection<WorkflowEdgeViewModel> Connections { get; }
    public WorkflowNodeViewModel SourceNode { get; private set; }
    public WorkflowNodeViewModel ThresholdNode { get; private set; }
    public WorkflowNodeViewModel MeasureNode { get; private set; }
    public ObservableCollection<string> Logs { get; }
    public ObservableCollection<RunHistoryItemViewModel> RunHistory { get; } = [];
    public ObservableCollection<ToolParameterViewModel> SelectedParameters { get; private set; } = [];
    public IReadOnlyList<HmiDataPointDescriptor> HmiBindingOptions =>
        HmiDataPointCatalog.GetAll();
    public HmiRuntimeState HmiRuntimeState => _hmiRuntimeState;
    public ICommand RunWorkflowCommand { get; }
    public ICommand StopWorkflowCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand AddRecordedCameraCommand { get; }
    public ICommand AddNativeCameraCommand { get; }
    public ICommand AddRecipeCommand { get; }
    public ICommand RefreshDeviceFeaturesCommand { get; }
    public ICommand SetDeviceFeatureCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand AcknowledgeAlarmCommand { get; }
    public ICommand ClearAlarmCommand { get; }
    public ICommand AddHmiWidgetCommand { get; }
    public ICommand OpenHmiRuntimeCommand { get; }

    public string HmiTitle
    {
        get => _project.Hmi.Title;
        set
        {
            if (string.Equals(_project.Hmi.Title, value, StringComparison.Ordinal))
            {
                return;
            }

            _project.Hmi.Title = value;
            OnPropertyChanged();
        }
    }

    public string HmiRefreshInterval
    {
        get => _project.Hmi.RefreshIntervalMilliseconds.ToString();
        set
        {
            if (!int.TryParse(value, out var interval))
            {
                return;
            }

            interval = Math.Clamp(interval, 100, 10_000);
            if (_project.Hmi.RefreshIntervalMilliseconds == interval)
            {
                return;
            }

            _project.Hmi.RefreshIntervalMilliseconds = interval;
            OnPropertyChanged();
        }
    }

    public DeviceItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                ((AsyncCommand)RefreshDeviceFeaturesCommand).RaiseCanExecuteChanged();
                ((AsyncCommand<object?>)SetDeviceFeatureCommand).RaiseCanExecuteChanged();
                DeviceFeatures.Clear();
            }
        }
    }

    public RecipeItemViewModel? SelectedRecipe
    {
        get => _selectedRecipe;
        private set
        {
            if (SetProperty(ref _selectedRecipe, value))
            {
                OnPropertyChanged(nameof(HasSelectedRecipe));
            }
        }
    }

    public bool HasSelectedDevice => SelectedDevice is not null;
    public bool HasSelectedRecipe => SelectedRecipe is not null;

    public string SelectedDeviceName
    {
        get => SelectedDevice?.Name ?? string.Empty;
        set
        {
            if (SelectedDevice is null || string.IsNullOrWhiteSpace(value) ||
                string.Equals(SelectedDevice.Name, value, StringComparison.Ordinal))
            {
                return;
            }

            Demand(Permission.EditDevices);
            SelectedDevice.UpdateName(value);
            OnPropertyChanged();
            RefreshProjectItems();
        }
    }

    public string SelectedDeviceSettings
    {
        get => SelectedDevice?.SettingsSummary ?? string.Empty;
        set
        {
            if (SelectedDevice is null)
            {
                return;
            }

            Demand(Permission.EditDevices);
            foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = item.IndexOf('=');
                if (separator > 0)
                {
                    UpdateSelectedDeviceSetting(
                        item[..separator].Trim(),
                        item[(separator + 1)..].Trim());
                }
            }

            OnPropertyChanged();
            RefreshProjectItems();
        }
    }

    public string SelectedRecipeName
    {
        get => SelectedRecipe?.Name ?? string.Empty;
        set
        {
            if (SelectedRecipe is null || string.IsNullOrWhiteSpace(value) ||
                string.Equals(SelectedRecipe.Name, value, StringComparison.Ordinal))
            {
                return;
            }

            Demand(Permission.EditRecipes);
            SelectedRecipe.UpdateName(value);
            OnPropertyChanged();
            RefreshProjectItems();
        }
    }

    public void UpdateSelectedDeviceName(string name)
    {
        Demand(Permission.EditDevices);
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SelectedDevice.UpdateName(name);
        RefreshProjectItems();
        SelectedDevice = Devices.FirstOrDefault(item => item.Id == SelectedDevice.Id);
    }

    public void UpdateSelectedDeviceSetting(string key, string value)
    {
        Demand(Permission.EditDevices);
        if (SelectedDevice is null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        SelectedDevice.UpdateSetting(key, value);
    }

    public void UpdateSelectedRecipeName(string name)
    {
        Demand(Permission.EditRecipes);
        if (SelectedRecipe is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SelectedRecipe.UpdateName(name);
        RefreshProjectItems();
        SelectedRecipe = Recipes.FirstOrDefault(item => item.Id == SelectedRecipe.Id);
    }

    public PortViewModel? PendingOutput
    {
        get => _pendingOutput;
        private set => SetProperty(ref _pendingOutput, value);
    }

    public WorkflowNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (ReferenceEquals(_selectedNode, value))
            {
                return;
            }

            if (_selectedNode is not null)
            {
                _selectedNode.IsSelected = false;
            }

            _selectedNode = value;
            if (_selectedNode is not null)
            {
                _selectedNode.IsSelected = true;
            }

            OnPropertyChanged();
        }
    }

    public string RuntimeStatus
    {
        get => _runtimeStatus;
        private set => SetProperty(ref _runtimeStatus, value);
    }

    public string RuntimeStatusColor
    {
        get => _runtimeStatusColor;
        private set => SetProperty(ref _runtimeStatusColor, value);
    }

    public string LastResult
    {
        get => _lastResult;
        private set => SetProperty(ref _lastResult, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public Bitmap? PreviewBitmap
    {
        get => _previewBitmap;
        private set => SetProperty(ref _previewBitmap, value);
    }

    public string PreviewSummary
    {
        get => _previewSummary;
        private set => SetProperty(ref _previewSummary, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RefreshCommandStates();
                OnPropertyChanged(nameof(HmiSummary));
                RefreshHmiRuntime();
            }
        }
    }

    private UserSession CreateStudioSession()
    {
        var userName = Environment.GetEnvironmentVariable("BOMAXING_STUDIO_USER") ?? "admin";
        var password = Environment.GetEnvironmentVariable("BOMAXING_STUDIO_PASSWORD") ??
                       Environment.GetEnvironmentVariable("BOMAXING_ADMIN_PASSWORD");
        if (!string.IsNullOrWhiteSpace(password) &&
            _runtime.AccessControl.Authenticate(userName, password, out var session) &&
            session is not null)
        {
            return session;
        }

        return new UserSession("local-operator", UserRole.Operator);
    }

    private bool Can(Permission permission) =>
        _runtime.AccessControl.Can(_session, permission);

    private void Demand(Permission permission) =>
        _runtime.AccessControl.Demand(_session, permission);

    private void Login()
    {
        if (_runtime.AccessControl.Authenticate(
                LoginUserName,
                LoginPassword,
                out var session) &&
            session is not null)
        {
            _session = session;
            LoginPassword = string.Empty;
            LoginStatus = $"Signed in as {session.UserName}";
            OnPropertyChanged(nameof(CurrentUserName));
            OnPropertyChanged(nameof(CurrentUserRole));
            OnPropertyChanged(nameof(CurrentUserSummary));
            OnPropertyChanged(nameof(IsAuthenticated));
            RefreshCommandStates();
            return;
        }

        LoginPassword = string.Empty;
        LoginStatus = "Login failed";
        AddLog($"Login failed: {LoginUserName}");
        RefreshCommandStates();
    }

    private void Logout()
    {
        _session = new UserSession("local-operator", UserRole.Operator);
        LoginUserName = _session.UserName;
        LoginPassword = string.Empty;
        LoginStatus = "Local operator session";
        OnPropertyChanged(nameof(CurrentUserName));
        OnPropertyChanged(nameof(CurrentUserRole));
        OnPropertyChanged(nameof(CurrentUserSummary));
        OnPropertyChanged(nameof(IsAuthenticated));
        RefreshCommandStates();
    }

    private async Task RunWorkflowAsync()
    {
        Demand(Permission.RunWorkflow);
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        var runStartedAt = Stopwatch.GetTimestamp();
        var historyRecorded = false;
        _runCancellation = new CancellationTokenSource();
        RuntimeStatus = "Running";
        RuntimeStatusColor = "#F0B45B";
        LastResult = "Running";
        ResultSummary = "Executing image source, threshold and measurement tools...";
        PreviewBitmap = null;
        PreviewSummary = "Rendering result preview...";
        Logs.Clear();
        foreach (var node in Nodes)
        {
            node.Reset();
        }

        var samplePath = Path.Combine(
            Path.GetTempPath(),
            $"bomaxing-studio-{Guid.NewGuid():N}.pgm");
        await WriteSampleImageAsync(samplePath);
        _sourceNode.Parameters["path"] = JsonSerializer.SerializeToElement(samplePath);

        try
        {
            ApplySelectedParameters();
            AddLog("Workflow started");
            _runtime.AuditTrail.Append("workflow.run", "studio", _workflow.Id);
            var result = await _runtime.Projects.RunWorkflowAsync(
                _project,
                _workflow.Id,
                cancellationToken: _runCancellation.Token);

            for (var index = 0; index < result.Records.Count; index++)
            {
                var record = result.Records[index];
                Nodes[index].Apply(record);
                AddLog(
                    $"{record.NodeName} {(record.Succeeded ? "completed" : "failed")} " +
                    $"· {record.Duration.TotalMilliseconds:F2} ms");
            }

            if (result.Succeeded)
            {
                UpdatePreview(result);
                var area = GetMeasurement(result, _measureNode.Id, "area");
                var centerX = GetMeasurement(result, _measureNode.Id, "centerX");
                var centerY = GetMeasurement(result, _measureNode.Id, "centerY");
                RuntimeStatus = "Ready";
                RuntimeStatusColor = "#45D6A7";
                LastResult = "SUCCESS";
                _lastArea = area.Value;
                _lastCenterX = centerX.Value;
                _lastCenterY = centerY.Value;
                _lastImage = result.Outputs.Values
                    .Select(value => value.Value)
                    .OfType<Image2D>()
                    .LastOrDefault();
                ResultSummary =
                    $"Area {area.Value:F2} {area.Unit} · " +
                    $"Center ({centerX.Value:F2}, {centerY.Value:F2}) {centerY.Unit}";
                RefreshHmiRuntime();
                AddLog("Workflow completed successfully");
                _runtime.AuditTrail.Append("workflow.completed", "studio", _workflow.Id);
                RecordRunHistory("SUCCESS", ResultSummary, Stopwatch.GetElapsedTime(runStartedAt));
                historyRecorded = true;
            }
            else
            {
                RuntimeStatus = "Faulted";
                RuntimeStatusColor = "#E36E7A";
                LastResult = "FAILED";
                ResultSummary = "The workflow stopped at the first failed node.";
                RefreshHmiRuntime();
                AddLog("Workflow failed");
                _runtime.Alarms.Raise(
                    new AlarmDefinition(
                        "WORKFLOW_FAILED",
                        "Workflow execution failed",
                    AlarmSeverity.Error),
                    source: _workflow.Id);
                RecordRunHistory("FAILED", ResultSummary, Stopwatch.GetElapsedTime(runStartedAt));
                historyRecorded = true;
            }
        }
        catch (Exception exception)
            when (exception is OperationCanceledException && _runCancellation?.IsCancellationRequested == true)
        {
            RuntimeStatus = "Stopped";
            RuntimeStatusColor = "#F0B45B";
            LastResult = "STOPPED";
            ResultSummary = "Workflow execution was cancelled.";
            RefreshHmiRuntime();
            AddLog("Workflow stopped");
            RecordRunHistory("STOPPED", ResultSummary, Stopwatch.GetElapsedTime(runStartedAt));
            historyRecorded = true;
        }
        catch (Exception exception)
        {
            RuntimeStatus = "Faulted";
            RuntimeStatusColor = "#E36E7A";
            LastResult = "FAILED";
            ResultSummary = exception.Message;
            RefreshHmiRuntime();
            AddLog($"Unhandled error: {exception.Message}");
            RecordRunHistory("FAILED", ResultSummary, Stopwatch.GetElapsedTime(runStartedAt));
            historyRecorded = true;
        }
        finally
        {
            if (!historyRecorded)
            {
                RecordRunHistory(LastResult, ResultSummary, Stopwatch.GetElapsedTime(runStartedAt));
            }
            File.Delete(samplePath);
            _runCancellation?.Dispose();
            _runCancellation = null;
            IsRunning = false;
            RefreshHmiRuntime();
        }
    }

    private void StopWorkflow()
    {
        Demand(Permission.RunWorkflow);
        _runCancellation?.Cancel();
        AddLog("Workflow stop requested");
    }

    private Task SaveProjectAsync()
    {
        return SaveProjectCoreAsync();
    }

    private async Task SaveProjectCoreAsync()
    {
        Demand(Permission.EditWorkflow);
        foreach (var node in _workflow.Nodes)
        {
            var viewModel = Nodes.FirstOrDefault(item => item.NodeId == node.Id);
            if (viewModel is null)
            {
                continue;
            }

            node.PositionX = viewModel.X;
            node.PositionY = viewModel.Y;
        }

        await _runtime.Projects.SaveProjectAsync(_project, _projectPath);
        AddLog($"Project saved: {_projectPath}");
    }

    private async Task OpenProjectAsync()
    {
        Demand(Permission.EditWorkflow);
        if (!File.Exists(_projectPath))
        {
            AddLog("No saved project found");
            return;
        }

        var loaded = await _runtime.Projects.LoadProjectAsync(_projectPath);
        var workflow = loaded.Workflows.FirstOrDefault();
        if (workflow is null)
        {
            AddLog("Project does not contain a workflow");
            return;
        }

        _project.Id = loaded.Id;
        _project.Name = loaded.Name;
        _projectName = loaded.Name;
        _workflowName = workflow.Name;
        _project.Description = loaded.Description;
        _project.Settings = loaded.Settings;
        _project.Devices = loaded.Devices;
        _project.Recipes = loaded.Recipes;
        _project.Hmi = loaded.Hmi;
        EnsureDefaultHmi();
        _project.Workflows = loaded.Workflows;
        _workflow = workflow;
        _sourceNode = workflow.Nodes.ElementAtOrDefault(0) ?? new WorkflowNodeDefinition();
        _thresholdNode = workflow.Nodes.ElementAtOrDefault(1) ?? _sourceNode;
        _measureNode = workflow.Nodes.ElementAtOrDefault(2) ?? _thresholdNode;
        _runHistoryPath = GetRunHistoryPath(_project.Id);
        Nodes.Clear();
        foreach (var node in workflow.Nodes.Select((node, index) => CreateNodeViewModel(node, index)))
        {
            Nodes.Add(node);
        }
        Connections.Clear();
        foreach (var connection in CreateEdgeViewModels())
        {
            Connections.Add(connection);
        }

        SourceNode = Nodes.ElementAtOrDefault(0) ?? CreateNodeViewModel(
            new WorkflowNodeDefinition { Name = "No source", ToolType = "unknown" }, 0);
        ThresholdNode = Nodes.ElementAtOrDefault(1) ?? SourceNode;
        MeasureNode = Nodes.ElementAtOrDefault(2) ?? ThresholdNode;
        RefreshProjectItems();
        RefreshHmiWidgets();
        _lastArea = null;
        _lastCenterX = null;
        _lastCenterY = null;
        _lastImage = null;
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WorkflowName));
        OnPropertyChanged(nameof(HmiTitle));
        OnPropertyChanged(nameof(HmiRefreshInterval));
        _ = LoadRunHistoryAsync();
        AddLog($"Project opened: {_projectPath}");
    }

    private void AddToolToWorkflow(ToolItemViewModel tool)
    {
        Demand(Permission.EditWorkflow);
        var before = CaptureProjectState();
        var node = _workflow.AddNode(tool.TypeId, tool.DisplayName);
        var index = Nodes.Count;
        node.PositionX = 55 + (index * 300);
        node.PositionY = 130;
        Nodes.Add(CreateNodeViewModel(node, index));
        RecordEdit(before);
        AddLog($"Added tool: {tool.DisplayName}");
    }

    private void RefreshProjectItems()
    {
        var selectedDeviceId = SelectedDevice?.Id;
        var selectedRecipeId = SelectedRecipe?.Id;
        _runtime.DeviceSessions.Configure(_project.Devices);
        Devices.Clear();
        foreach (var device in _project.Devices)
        {
            Devices.Add(new DeviceItemViewModel(
                device,
                _runtime.DeviceSessions.GetState(device.Id),
                SelectDevice,
                ConnectDeviceAsync,
                DisconnectDeviceAsync,
                CaptureDeviceAsync,
                () => Can(Permission.EditDevices)));
        }

        Recipes.Clear();
        foreach (var recipe in _project.Recipes)
        {
            Recipes.Add(new RecipeItemViewModel(recipe, SelectRecipe));
        }

        SelectedDevice = selectedDeviceId is null
            ? null
            : Devices.FirstOrDefault(item => item.Id == selectedDeviceId);
        SelectedRecipe = selectedRecipeId is null
            ? null
            : Recipes.FirstOrDefault(item => item.Id == selectedRecipeId);
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(SelectedDeviceSettings));
        OnPropertyChanged(nameof(SelectedRecipeName));
        OnPropertyChanged(nameof(OnlineDeviceCount));
        OnPropertyChanged(nameof(HmiSummary));
    }

    private void EnsureDefaultHmi()
    {
        _project.Hmi ??= new HmiDefinition();
        if (_project.Hmi.Widgets.Count > 0)
        {
            return;
        }

        _project.Hmi.Widgets.Add(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.Status,
            Title = "运行状态",
            Binding = "runtime.status",
            X = 0,
            Y = 0,
            Width = 260,
            Height = 100
        });
        _project.Hmi.Widgets.Add(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.Number,
            Title = "最新面积",
            Binding = "workflow.last.area",
            X = 280,
            Y = 0,
            Width = 220,
            Height = 100
        });
        _project.Hmi.Widgets.Add(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.AlarmList,
            Title = "活动报警",
            Binding = "alarms.active",
            X = 0,
            Y = 120,
            Width = 500,
            Height = 220
        });
        _project.Hmi.Widgets.Add(new HmiWidgetDefinition
        {
            Kind = HmiWidgetKind.Trend,
            Title = "运行趋势",
            Binding = "history.duration",
            X = 520,
            Y = 120,
            Width = 420,
            Height = 220
        });
    }

    private void RefreshHmiWidgets()
    {
        HmiWidgets.Clear();
        foreach (var widget in _project.Hmi.Widgets)
        {
            var viewModel = new HmiWidgetItemViewModel(
                widget,
                RemoveHmiWidget,
                () => !IsRunning && Can(Permission.EditWorkflow));
            viewModel.RefreshRuntime(_hmiRuntimeState);
            HmiWidgets.Add(viewModel);
        }
    }

    private void AddHmiWidget()
    {
        Demand(Permission.EditWorkflow);
        var widget = new HmiWidgetDefinition
        {
            Title = $"组件 {_project.Hmi.Widgets.Count + 1}",
            Binding = "runtime.status",
            X = 0,
            Y = 360 + (_project.Hmi.Widgets.Count * 20)
        };
        _project.Hmi.Widgets.Add(widget);
        var viewModel = new HmiWidgetItemViewModel(
            widget,
            RemoveHmiWidget,
            () => !IsRunning && Can(Permission.EditWorkflow));
        viewModel.RefreshRuntime(_hmiRuntimeState);
        HmiWidgets.Add(viewModel);
    }

    private void RemoveHmiWidget(HmiWidgetItemViewModel item)
    {
        Demand(Permission.EditWorkflow);
        _project.Hmi.Widgets.Remove(item.Definition);
        HmiWidgets.Remove(item);
    }

    private async Task ConnectDeviceAsync(DeviceItemViewModel device)
    {
        Demand(Permission.EditDevices);
        try
        {
            await _runtime.DeviceSessions.ConnectAsync(device.Id);
            device.UpdateState(_runtime.DeviceSessions.GetState(device.Id));
            OnPropertyChanged(nameof(OnlineDeviceCount));
            OnPropertyChanged(nameof(HmiSummary));
            AddLog($"Connected device: {device.Name}");
            _runtime.AuditTrail.Append("device.connect", "studio", device.Id);
            await RefreshDeviceFeaturesAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            device.UpdateState(DeviceState.Faulted);
            AddLog($"Device connect failed: {exception.Message}");
            _runtime.Alarms.Raise(
                new AlarmDefinition(
                    "DEVICE_CONNECT_FAILED",
                    $"Device '{device.Name}' could not connect",
                    AlarmSeverity.Error),
                device.Id);
        }
    }

    private async Task DisconnectDeviceAsync(DeviceItemViewModel device)
    {
        Demand(Permission.EditDevices);
        try
        {
            await _runtime.DeviceSessions.DisconnectAsync(device.Id);
            device.UpdateState(_runtime.DeviceSessions.GetState(device.Id));
            OnPropertyChanged(nameof(OnlineDeviceCount));
            OnPropertyChanged(nameof(HmiSummary));
            AddLog($"Disconnected device: {device.Name}");
            _runtime.AuditTrail.Append("device.disconnect", "studio", device.Id);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            AddLog($"Device disconnect failed: {exception.Message}");
        }
    }

    private async Task CaptureDeviceAsync(DeviceItemViewModel device)
    {
        Demand(Permission.EditDevices);
        try
        {
            var frame = await _runtime.DeviceSessions.CaptureAsync(
                device.Id,
                new CaptureRequest(IncludeImage: true));
            device.UpdateCapture(frame);
            AddLog($"Captured device frame: {device.Name} #{frame.Metadata.Sequence}");
            _runtime.AuditTrail.Append("device.capture", "studio", device.Id);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            AddLog($"Device capture failed: {exception.Message}");
            _runtime.Alarms.Raise(
                new AlarmDefinition(
                    "DEVICE_CAPTURE_FAILED",
                    $"Device '{device.Name}' capture failed",
                    AlarmSeverity.Error),
                device.Id);
        }
    }

    private async Task RefreshDeviceFeaturesAsync()
    {
        Demand(Permission.EditDevices);
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            var features = await _runtime.DeviceSessions.GetFeaturesAsync(SelectedDevice.Id);
            DeviceFeatures.Clear();
            foreach (var feature in features)
            {
                DeviceFeatures.Add(new DeviceFeatureItemViewModel(
                    feature,
                    SetDeviceFeatureAsync,
                    () => Can(Permission.EditDevices)));
            }

            AddLog($"Loaded {DeviceFeatures.Count} device features: {SelectedDevice.Name}");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            AddLog($"Device feature read failed: {exception.Message}");
            _runtime.Alarms.Raise(
                new AlarmDefinition(
                    "DEVICE_FEATURE_READ_FAILED",
                    $"Device '{SelectedDevice.Name}' features could not be read",
                    AlarmSeverity.Warning),
                SelectedDevice.Id);
        }
    }

    private async Task SetDeviceFeatureAsync(object? parameter)
    {
        Demand(Permission.EditDevices);
        if (SelectedDevice is null || parameter is not DeviceFeatureItemViewModel feature)
        {
            return;
        }

        try
        {
            await _runtime.DeviceSessions.SetFeatureAsync(
                SelectedDevice.Id,
                feature.Name,
                feature.Value);
            AddLog($"Updated device feature: {feature.Name}={feature.Value}");
            _runtime.AuditTrail.Append("device.feature.set", "studio", SelectedDevice.Id);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            AddLog($"Device feature write failed: {exception.Message}");
            _runtime.Alarms.Raise(
                new AlarmDefinition(
                    "DEVICE_FEATURE_WRITE_FAILED",
                    $"Device '{SelectedDevice.Name}' feature update failed",
                    AlarmSeverity.Error),
                SelectedDevice.Id);
        }
    }

    private void AddRecordedCamera()
    {
        Demand(Permission.EditDevices);
        var configuration = new BoMaxing.Core.Devices.DeviceConfiguration
        {
            Name = "Recorded Camera",
            TypeId = "replay.camera"
        };
        configuration.Settings["recordingPath"] = JsonSerializer.SerializeToElement(string.Empty);
        configuration.Settings["loop"] = JsonSerializer.SerializeToElement(true);
        _project.Devices.Add(configuration);
        RefreshProjectItems();
        SelectedDevice = Devices.LastOrDefault();
        AddLog("Added recorded camera device");
    }

    private void AddNativeCamera()
    {
        Demand(Permission.EditDevices);
        var configuration = new BoMaxing.Core.Devices.DeviceConfiguration
        {
            Name = "Native 3D Camera",
            TypeId = "native.camera3d"
        };
        configuration.Settings["executablePath"] = JsonSerializer.SerializeToElement(string.Empty);
        _project.Devices.Add(configuration);
        RefreshProjectItems();
        SelectedDevice = Devices.LastOrDefault();
        AddLog("Added native driver camera device");
    }

    private void AddRecipe()
    {
        Demand(Permission.EditRecipes);
        var recipe = new RecipeDefinition
        {
            Name = $"Recipe {_project.Recipes.Count + 1}"
        };
        _project.Recipes.Add(recipe);
        RefreshProjectItems();
        SelectedRecipe = Recipes.LastOrDefault();
        AddLog($"Added recipe: {recipe.Name}");
    }

    private void SelectDevice(DeviceItemViewModel device)
    {
        SelectedDevice = device;
        SelectedRecipe = null;
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(SelectedDeviceSettings));
        OnPropertyChanged(nameof(SelectedRecipeName));
    }

    private void SelectRecipe(RecipeItemViewModel recipe)
    {
        SelectedRecipe = recipe;
        SelectedDevice = null;
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(SelectedDeviceSettings));
        OnPropertyChanged(nameof(SelectedRecipeName));
    }

    private void OnAlarmChanged(object? sender, AlarmEvent alarm)
    {
        var item = Alarms.FirstOrDefault(value => value.Code == alarm.Definition.Code);
        if (item is null)
        {
            Alarms.Insert(0, new AlarmItemViewModel(
                alarm,
                AcknowledgeAlarm,
                ClearAlarm,
                () => Can(Permission.AcknowledgeAlarm)));
        }
        else
        {
            item.Update(alarm);
        }

        OnPropertyChanged(nameof(ActiveAlarmCount));
        OnPropertyChanged(nameof(HmiSummary));
        RefreshHmiRuntime();
        RefreshCommandStates();
    }

    private void AcknowledgeAlarm(AlarmItemViewModel? item)
    {
        Demand(Permission.AcknowledgeAlarm);
        if (item is null || item.State == AlarmState.Cleared)
        {
            return;
        }

        _runtime.Alarms.Acknowledge(item.Code);
        _runtime.AuditTrail.Append("alarm.acknowledge", "studio", item.Code);
    }

    private void ClearAlarm(AlarmItemViewModel? item)
    {
        Demand(Permission.AcknowledgeAlarm);
        if (item is null || item.State == AlarmState.Cleared)
        {
            return;
        }

        try
        {
            _runtime.Alarms.Clear(item.Code);
            _runtime.AuditTrail.Append("alarm.clear", "studio", item.Code);
        }
        catch (InvalidOperationException exception)
        {
            AddLog(exception.Message);
        }
    }

    public void MoveNode(WorkflowNodeViewModel node, double x, double y)
    {
        Demand(Permission.EditWorkflow);
        ArgumentNullException.ThrowIfNull(node);
        node.MoveTo(Math.Max(0, x), Math.Max(0, y));
        var definition = _workflow.Nodes.FirstOrDefault(item => item.Id == node.NodeId);
        if (definition is not null)
        {
            definition.PositionX = node.X;
            definition.PositionY = node.Y;
        }
        Connections.Clear();
        foreach (var connection in CreateEdgeViewModels())
        {
            Connections.Add(connection);
        }
    }

    public void BeginNodeMove()
    {
        Demand(Permission.EditWorkflow);
        _nodeMoveBefore ??= CaptureProjectState();
    }

    public void EndNodeMove()
    {
        if (_nodeMoveBefore is null)
        {
            return;
        }

        var before = _nodeMoveBefore;
        _nodeMoveBefore = null;
        if (!ProjectStatesEqual(before, CaptureProjectState()))
        {
            RecordEdit(before);
        }
    }

    public void StartOutputConnection(PortViewModel port)
    {
        Demand(Permission.EditWorkflow);
        if (port.Direction == PortDirection.Output)
        {
            PendingOutput = port;
        }
    }

    public void CompleteInputConnection(PortViewModel port)
    {
        Demand(Permission.EditWorkflow);
        if (PendingOutput is null || port.Direction != PortDirection.Input ||
            PendingOutput.NodeId == port.NodeId)
        {
            return;
        }

        if (PendingOutput.DataType != DataType.Unknown &&
            port.DataType != DataType.Unknown &&
            PendingOutput.DataType != port.DataType)
        {
            AddLog($"Cannot connect {PendingOutput.DataType} to {port.DataType}");
            PendingOutput = null;
            return;
        }

        var before = CaptureProjectState();
        if (!_workflow.Edges.Any(edge =>
                edge.FromNodeId == PendingOutput.NodeId &&
                edge.FromPort == PendingOutput.Name &&
                edge.ToNodeId == port.NodeId &&
                edge.ToPort == port.Name))
        {
            _workflow.Connect(PendingOutput.NodeId, PendingOutput.Name, port.NodeId, port.Name);
            Connections.Clear();
            foreach (var connection in CreateEdgeViewModels())
            {
                Connections.Add(connection);
            }
            RecordEdit(before);
            AddLog($"Connected {PendingOutput.DisplayName} -> {port.DisplayName}");
        }

        PendingOutput = null;
    }

    private void Undo()
    {
        if (_undoHistory.TryPop(out var edit))
        {
            _redoHistory.Push(edit);
            RestoreProjectState(edit.Before);
        }
    }

    private void Redo()
    {
        if (_redoHistory.TryPop(out var edit))
        {
            _undoHistory.Push(edit);
            RestoreProjectState(edit.After);
        }
    }

    private void RecordEdit(ProjectState before)
    {
        _undoHistory.Push(new ProjectEdit(CaptureProjectState(), before));
        _redoHistory.Clear();
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
    }

    private ProjectState CaptureProjectState() => new(
        _workflow.Nodes.Select(node => new WorkflowNodeDefinition
        {
            Id = node.Id,
            Name = node.Name,
            ToolType = node.ToolType,
            PositionX = node.PositionX,
            PositionY = node.PositionY,
            Parameters = node.Parameters.ToDictionary(item => item.Key, item => item.Value)
        }).ToList(),
        _workflow.Edges.Select(edge => new WorkflowEdgeDefinition
        {
            FromNodeId = edge.FromNodeId,
            FromPort = edge.FromPort,
            ToNodeId = edge.ToNodeId,
            ToPort = edge.ToPort
        }).ToList());

    private void RestoreProjectState(ProjectState state)
    {
        _workflow.Nodes = state.Nodes;
        _workflow.Edges = state.Edges;
        _sourceNode = _workflow.Nodes.ElementAtOrDefault(0) ?? new WorkflowNodeDefinition();
        _thresholdNode = _workflow.Nodes.ElementAtOrDefault(1) ?? _sourceNode;
        _measureNode = _workflow.Nodes.ElementAtOrDefault(2) ?? _thresholdNode;
        RebuildWorkflowView();
    }

    private void RebuildWorkflowView()
    {
        Nodes.Clear();
        foreach (var node in _workflow.Nodes.Select((node, index) => CreateNodeViewModel(node, index)))
        {
            Nodes.Add(node);
        }
        Connections.Clear();
        foreach (var connection in CreateEdgeViewModels())
        {
            Connections.Add(connection);
        }
        SourceNode = Nodes.ElementAtOrDefault(0) ?? CreateNodeViewModel(new WorkflowNodeDefinition(), 0);
        ThresholdNode = Nodes.ElementAtOrDefault(1) ?? SourceNode;
        MeasureNode = Nodes.ElementAtOrDefault(2) ?? ThresholdNode;
        SelectedNode = null;
        SelectedParameters = [];
        OnPropertyChanged(nameof(SelectedParameters));
    }

    private static bool ProjectStatesEqual(ProjectState left, ProjectState right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private WorkflowNodeViewModel CreateNodeViewModel(
        WorkflowNodeDefinition node,
        int index)
    {
        var descriptor = _runtime.ToolRegistry.Create(node.ToolType).Descriptor;
        return new WorkflowNodeViewModel(
            node,
            index,
            descriptor,
            SelectNode,
            StartOutputConnection,
            CompleteInputConnection);
    }

    public void SelectNodeFromView(WorkflowNodeViewModel node) => SelectNode(node);

    private void SelectNode(WorkflowNodeViewModel node)
    {
        SelectedNode = node;
        SelectedParameters = new ObservableCollection<ToolParameterViewModel>(
            _runtime.ToolRegistry.Create(node.ToolType).Descriptor.Parameters.Select(parameter =>
                new ToolParameterViewModel(
                    parameter,
                    _workflow.Nodes.First(item => item.Id == node.NodeId).Parameters.TryGetValue(
                        parameter.Name,
                        out var value)
                        ? value
                        : null)));
        OnPropertyChanged(nameof(SelectedParameters));
    }

    private IEnumerable<WorkflowEdgeViewModel> CreateEdgeViewModels()
    {
        var positions = _workflow.Nodes
            .Select((node, index) => (
                node.Id,
                X: node.PositionX ?? (55d + (index * 300d)),
                Y: node.PositionY ?? 130d))
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var edge in _workflow.Edges)
        {
            if (!positions.TryGetValue(edge.FromNodeId, out var from) ||
                !positions.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            yield return new WorkflowEdgeViewModel(
                new Point(from.X + 245, from.Y + 60),
                new Point(to.X, to.Y + 60));
        }
    }

    private void ApplySelectedParameters()
    {
        if (SelectedNode is null)
        {
            return;
        }

        var node = _workflow.Nodes.FirstOrDefault(item => item.Id == SelectedNode.NodeId);
        if (node is null)
        {
            return;
        }

        foreach (var parameter in SelectedParameters)
        {
            if (parameter.Definition.Type == ToolParameterType.Number &&
                double.TryParse(parameter.Value, out var number))
            {
                node.Parameters[parameter.Name] = JsonSerializer.SerializeToElement(number);
            }
            else
            {
                node.Parameters[parameter.Name] = JsonSerializer.SerializeToElement(parameter.Value);
            }
        }
    }

    private void AddLog(string message)
    {
        Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    private void RecordRunHistory(string status, string summary, TimeSpan duration)
    {
        var entry = new WorkflowRunHistoryEntry(
            DateTimeOffset.Now,
            _workflow.Id,
            status,
            duration.TotalMilliseconds,
            summary);
        _runHistoryEntries.Insert(0, entry);
        if (_runHistoryEntries.Count > 5000)
        {
            _runHistoryEntries.RemoveAt(_runHistoryEntries.Count - 1);
        }

        RunHistory.Insert(
            0,
            new RunHistoryItemViewModel(
                entry.OccurredAt,
                entry.Status,
                entry.Summary,
                TimeSpan.FromMilliseconds(entry.DurationMilliseconds)));
        while (RunHistory.Count > 20)
        {
            RunHistory.RemoveAt(RunHistory.Count - 1);
        }

        OnPropertyChanged(nameof(RunCount));
        OnPropertyChanged(nameof(SuccessfulRunCount));
        OnPropertyChanged(nameof(SuccessRate));
        OnPropertyChanged(nameof(AverageRunDuration));
        OnPropertyChanged(nameof(TrendSummary));
        RefreshHmiRuntime();
        _ = SaveRunHistoryAsync();
    }

    private async Task LoadRunHistoryAsync()
    {
        try
        {
            var entries = await _runHistoryStore.LoadAsync(_runHistoryPath, _project.Id);
            _runHistoryEntries.Clear();
            _runHistoryEntries.AddRange(entries);
            RunHistory.Clear();
            foreach (var entry in entries.Take(20))
            {
                RunHistory.Add(new RunHistoryItemViewModel(
                    entry.OccurredAt,
                    entry.Status,
                    entry.Summary,
                    TimeSpan.FromMilliseconds(entry.DurationMilliseconds)));
            }

            OnPropertyChanged(nameof(RunCount));
            OnPropertyChanged(nameof(SuccessfulRunCount));
            OnPropertyChanged(nameof(SuccessRate));
            OnPropertyChanged(nameof(AverageRunDuration));
            OnPropertyChanged(nameof(TrendSummary));
            RefreshHmiRuntime();
        }
        catch (Exception exception)
        {
            AddLog($"Run history load failed: {exception.Message}");
        }
    }

    private async Task SaveRunHistoryAsync()
    {
        try
        {
            await _runHistoryStore.SaveAsync(
                _runHistoryPath,
                _project.Id,
                _runHistoryEntries);
        }
        catch (Exception exception)
        {
            AddLog($"Run history save failed: {exception.Message}");
        }
    }

    private static string GetRunHistoryPath(string projectId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BoMaxing",
        "history",
        $"{projectId}.json");

    private void RefreshHmiRuntime()
    {
        _hmiRuntimeState = new HmiRuntimeState
        {
            RuntimeStatus = RuntimeStatus,
            IsRunning = IsRunning,
            LastResult = LastResult,
            ResultSummary = ResultSummary,
            LastArea = _lastArea,
            LastCenterX = _lastCenterX,
            LastCenterY = _lastCenterY,
            LastImage = _lastImage,
            ActiveAlarms = _runtime.Alarms.GetAll(),
            History = _runHistoryEntries.ToArray(),
            DurationTrend = WorkflowRunHistoryQueryService.BuildTrend(
                _runHistoryEntries,
                TimeSpan.FromMinutes(5))
        };

        foreach (var widget in HmiWidgets)
        {
            widget.RefreshRuntime(_hmiRuntimeState);
        }

        OnPropertyChanged(nameof(HmiRuntimeState));
    }

    private void RefreshCommandStates()
    {
        ((AsyncCommand)RunWorkflowCommand).RaiseCanExecuteChanged();
        ((RelayCommand)StopWorkflowCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveProjectCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenProjectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UndoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RedoCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddRecordedCameraCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddNativeCameraCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddRecipeCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)RefreshDeviceFeaturesCommand).RaiseCanExecuteChanged();
        ((AsyncCommand<object?>)SetDeviceFeatureCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LoginCommand).RaiseCanExecuteChanged();
        ((RelayCommand)LogoutCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AcknowledgeAlarmCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearAlarmCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddHmiWidgetCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanEditWorkflow));
        OnPropertyChanged(nameof(CanEditDevices));
        OnPropertyChanged(nameof(CanEditRecipes));
        OnPropertyChanged(nameof(CanAcknowledgeAlarm));
        foreach (var tool in Tools)
        {
            tool.RefreshCommandState();
        }
        foreach (var device in Devices)
        {
            device.RefreshCommandState();
        }
        foreach (var alarm in Alarms)
        {
            alarm.RefreshCommandState();
        }
        foreach (var widget in HmiWidgets)
        {
            widget.RefreshCommandState();
        }
    }

    private void UpdatePreview(WorkflowExecutionResult result)
    {
        var image = result.Outputs.Values
            .Select(value => value.Value)
            .OfType<Image2D>()
            .LastOrDefault();
        if (image is not null)
        {
            PreviewBitmap = PreviewBitmapFactory.FromImage(image);
            var summary = FramePreview.Summarize(image);
            PreviewSummary =
                $"Image {summary.Width}×{summary.Height} · " +
                $"Gray {summary.Minimum}..{summary.Maximum} · Mean {summary.Mean:F1}";
            return;
        }

        var region = result.Outputs.Values
            .Select(value => value.Value)
            .OfType<Region2D>()
            .LastOrDefault();
        if (region is not null)
        {
            PreviewBitmap = PreviewBitmapFactory.FromRegion(region);
            PreviewSummary =
                $"Region {region.Width}×{region.Height} · Area {region.Area} px";
            return;
        }

        var depth = result.Outputs.Values
            .Select(value => value.Value)
            .OfType<DepthMap>()
            .LastOrDefault();
        if (depth is not null)
        {
            PreviewBitmap = PreviewBitmapFactory.FromDepth(depth);
            var summary = FramePreview.Summarize(depth);
            PreviewSummary =
                $"Depth {summary.Width}×{summary.Height} · " +
                $"{summary.Minimum:F3}..{summary.Maximum:F3} {summary.Unit}";
            return;
        }

        var cloud = result.Outputs.Values
            .Select(value => value.Value)
            .OfType<PointCloud3D>()
            .LastOrDefault();
        if (cloud is not null)
        {
            var summary = FramePreview.Summarize(cloud);
            PreviewBitmap = null;
            PreviewSummary = $"Point cloud {summary.PointCount} points · {summary.CoordinateSystem}";
            return;
        }

        PreviewBitmap = null;
        PreviewSummary = "No image or region output";
    }

    private static Measurement GetMeasurement(
        WorkflowExecutionResult result,
        string nodeId,
        string portName)
    {
        return result.Outputs[$"{nodeId}.{portName}"].Value as Measurement
            ?? throw new InvalidDataException($"Missing measurement output '{portName}'.");
    }

    private static async Task WriteSampleImageAsync(string path)
    {
        const string header = "P2\n# BoMaxing Studio image\n8 6\n255\n";
        var pixels = new[]
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 120, 140, 160, 180, 0, 0, 0,
            0, 120, 140, 160, 180, 0, 0, 0,
            0, 120, 140, 160, 180, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };
        await File.WriteAllTextAsync(
            path,
            header + string.Join(' ', pixels) + Environment.NewLine);
    }
}

public sealed class ToolItemViewModel
{
    public ToolItemViewModel(
        ToolDescriptor descriptor,
        Action<ToolItemViewModel> addToWorkflow,
        Func<bool> canAdd)
    {
        DisplayName = descriptor.DisplayName;
        Category = descriptor.Category.ToUpperInvariant();
        TypeId = descriptor.TypeId;
        AddCommand = new RelayCommand(_ => addToWorkflow(this), canAdd);
        _canAdd = canAdd;
    }

    private readonly Func<bool> _canAdd;

    public string DisplayName { get; }
    public string Category { get; }
    public string TypeId { get; }
    public ICommand AddCommand { get; }

    public void RefreshCommandState() => ((RelayCommand)AddCommand).RaiseCanExecuteChanged();
}

public sealed record RunHistoryItemViewModel(
    DateTimeOffset OccurredAt,
    string Status,
    string Summary,
    TimeSpan Duration)
{
    public string TimeText => OccurredAt.LocalDateTime.ToString("HH:mm:ss");
    public string DurationText => $"{Duration.TotalMilliseconds:F1} ms";
}

public sealed class DeviceItemViewModel : ViewModelBase
{
    private readonly BoMaxing.Core.Devices.DeviceConfiguration _configuration;
    private DeviceState _state;
    private string _stateText = string.Empty;
    private string _captureText = "No capture";
    private readonly Func<bool> _canEdit;

    public DeviceItemViewModel(
        BoMaxing.Core.Devices.DeviceConfiguration configuration,
        DeviceState state,
        Action<DeviceItemViewModel> select,
        Func<DeviceItemViewModel, Task> connect,
        Func<DeviceItemViewModel, Task> disconnect,
        Func<DeviceItemViewModel, Task> capture,
        Func<bool> canEdit)
    {
        _canEdit = canEdit;
        _configuration = configuration;
        Id = configuration.Id;
        TypeId = configuration.TypeId;
        SelectCommand = new RelayCommand(_ => select(this));
        ConnectCommand = new RelayCommand(_ => _ = connect(this), canEdit);
        DisconnectCommand = new RelayCommand(_ => _ = disconnect(this), canEdit);
        CaptureCommand = new RelayCommand(_ => _ = capture(this), canEdit);
        UpdateState(state);
    }

    public string Name => _configuration.Name;
    public string Id { get; }
    public string TypeId { get; }
    public string Summary => $"{Name} · {TypeId}";
    public DeviceState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public string CaptureText
    {
        get => _captureText;
        private set => SetProperty(ref _captureText, value);
    }
    public string SettingsSummary =>
        string.Join(", ", _configuration.Settings.Select(setting =>
            $"{setting.Key}={setting.Value.ToString()}"));
    public ICommand SelectCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand CaptureCommand { get; }

    public void RefreshCommandState()
    {
        ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)DisconnectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CaptureCommand).RaiseCanExecuteChanged();
    }

    public void UpdateState(DeviceState state)
    {
        State = state;
        StateText = state.ToString();
    }

    public void UpdateCapture(CameraFrame frame)
    {
        CaptureText = $"Frame #{frame.Metadata.Sequence} · {frame.Metadata.Width}×{frame.Metadata.Height}";
    }

    public void UpdateName(string name)
    {
        _configuration.Name = name;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Summary));
    }

    public void UpdateSetting(string key, string value)
    {
        _configuration.Settings[key] = JsonSerializer.SerializeToElement(value);
    }
}

public sealed class DeviceFeatureItemViewModel : ViewModelBase
{
    private string _value;
    private readonly Func<bool> _canEdit;

    public DeviceFeatureItemViewModel(
        DeviceFeature feature,
        Func<DeviceFeatureItemViewModel, Task> setFeature,
        Func<bool> canEdit)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(setFeature);
        _canEdit = canEdit ?? throw new ArgumentNullException(nameof(canEdit));
        Name = feature.Name;
        _value = feature.Value;
        IsReadOnly = feature.IsReadOnly;
        SetCommand = new RelayCommand(
            _ => _ = setFeature(this),
            () => !IsReadOnly && canEdit());
    }

    public string Name { get; }
    public bool IsReadOnly { get; }
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public ICommand SetCommand { get; }

    public void RefreshCommandState() => ((RelayCommand)SetCommand).RaiseCanExecuteChanged();
}

public sealed class RecipeItemViewModel
{
    private readonly RecipeDefinition _recipe;

    public RecipeItemViewModel(RecipeDefinition recipe, Action<RecipeItemViewModel> select)
    {
        _recipe = recipe;
        Id = recipe.Id;
        Name = recipe.Name;
        Summary = recipe.IsActive ? "Active recipe" : "Inactive recipe";
        SelectCommand = new RelayCommand(_ => select(this));
    }

    public string Name { get; }
    public string Id { get; }
    public string Summary { get; }
    public string ParametersSummary =>
        _recipe.Parameters.Count == 0
            ? "No parameters"
            : string.Join(", ", _recipe.Parameters.Select(parameter =>
                $"{parameter.Key}={parameter.Value}"));
    public ICommand SelectCommand { get; }

    public void UpdateName(string name)
    {
        _recipe.Name = name;
    }
}

public sealed class HmiWidgetItemViewModel : ViewModelBase
{
    private readonly Func<bool> _canEdit;
    private string _title;
    private string _binding;
    private HmiResolvedValue _resolvedValue = new(
        string.Empty,
        HmiDataPointType.Text,
        null,
        "No binding",
        false,
        "Select a data point.");
    private HmiBindingValidation _bindingValidation = new(
        false,
        "Select a data point.");

    public HmiWidgetItemViewModel(
        HmiWidgetDefinition definition,
        Action<HmiWidgetItemViewModel> remove,
        Func<bool> canEdit)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _title = definition.Title;
        _binding = definition.Binding;
        _canEdit = canEdit ?? throw new ArgumentNullException(nameof(canEdit));
        RemoveCommand = new RelayCommand(_ => remove(this), canEdit);
    }

    public HmiWidgetDefinition Definition { get; }
    public string Id => Definition.Id;
    public string KindText => Definition.Kind.ToString();
    public HmiWidgetKind Kind => Definition.Kind;
    public int X => Definition.X;
    public int Y => Definition.Y;
    public int Width => Definition.Width;
    public int Height => Definition.Height;

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                Definition.Title = value;
            }
        }
    }

    public string Binding
    {
        get => _binding;
        set
        {
            if (SetProperty(ref _binding, value))
            {
                Definition.Binding = value;
                OnPropertyChanged(nameof(SelectedBinding));
                OnPropertyChanged(nameof(BindingStatus));
                RefreshRuntime(_runtimeState);
            }
        }
    }

    private HmiRuntimeState _runtimeState = new();

    public IReadOnlyList<HmiDataPointDescriptor> BindingOptions =>
        HmiDataPointCatalog.GetAll()
            .Where(point => HmiDataPointCatalog.IsCompatible(Definition.Kind, point.Type))
            .ToArray();

    public HmiDataPointDescriptor? SelectedBinding
    {
        get => HmiDataPointCatalog.TryGet(Binding, out var descriptor)
            ? descriptor
            : null;
        set
        {
            var binding = value?.Binding ?? string.Empty;
            if (!string.Equals(Binding, binding, StringComparison.Ordinal))
            {
                Binding = binding;
            }
            else
            {
                OnPropertyChanged(nameof(BindingStatus));
                RefreshRuntime(_runtimeState);
            }
        }
    }

    public string BindingStatus
    {
        get
        {
            if (!_bindingValidation.IsValid)
            {
                return $"Invalid · {_bindingValidation.Message}";
            }

            return _resolvedValue.IsValid
                ? $"OK · {_resolvedValue.DisplayValue}"
                : $"Waiting · {_resolvedValue.Message}";
        }
    }

    public string ResolvedValue => _resolvedValue.DisplayValue;

    public ICommand RemoveCommand { get; }

    public void RefreshRuntime(HmiRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _runtimeState = state;
        _bindingValidation = HmiDataPointCatalog.Validate(Definition);
        _resolvedValue = HmiBindingResolver.Resolve(Binding, state);
        OnPropertyChanged(nameof(BindingOptions));
        OnPropertyChanged(nameof(SelectedBinding));
        OnPropertyChanged(nameof(BindingStatus));
        OnPropertyChanged(nameof(ResolvedValue));
    }

    public void RefreshCommandState() =>
        ((RelayCommand)RemoveCommand).RaiseCanExecuteChanged();
}

public sealed class AlarmItemViewModel : ViewModelBase
{
    private AlarmState _state;
    private string _stateText = string.Empty;
    private readonly Func<bool> _canAcknowledge;

    public AlarmItemViewModel(
        AlarmEvent alarm,
        Action<AlarmItemViewModel> acknowledge,
        Action<AlarmItemViewModel> clear,
        Func<bool> canAcknowledge)
    {
        _canAcknowledge = canAcknowledge ?? throw new ArgumentNullException(nameof(canAcknowledge));
        Code = alarm.Definition.Code;
        Message = alarm.Definition.Message;
        Severity = alarm.Definition.Severity.ToString();
        AcknowledgeCommand = new RelayCommand(
            _ => acknowledge(this),
            canAcknowledge);
        ClearCommand = new RelayCommand(
            _ => clear(this),
            canAcknowledge);
        Update(alarm);
    }

    public string Code { get; }
    public string Message { get; }
    public string Severity { get; }
    public ICommand AcknowledgeCommand { get; }
    public ICommand ClearCommand { get; }
    public AlarmState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public void Update(AlarmEvent alarm)
    {
        State = alarm.State;
        StateText = alarm.State.ToString();
    }

    public void RefreshCommandState()
    {
        ((RelayCommand)AcknowledgeCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearCommand).RaiseCanExecuteChanged();
    }
}

public sealed class WorkflowNodeViewModel : ViewModelBase
{
    private string _status = "Not run";
    private string _statusColor = "#8194A2";
    private string _duration = "--";
    private bool _isSelected;
    private double _x;
    private double _y;

    public WorkflowNodeViewModel(
        WorkflowNodeDefinition node,
        int index,
        ToolDescriptor descriptor,
        Action<WorkflowNodeViewModel> select,
        Action<PortViewModel> startOutput,
        Action<PortViewModel> completeInput)
    {
        NodeId = node.Id;
        Name = node.Name;
        ToolType = node.ToolType;
        Category = node.ToolType.StartsWith("vision.", StringComparison.OrdinalIgnoreCase)
            ? "VISION"
            : node.ToolType.StartsWith("io.", StringComparison.OrdinalIgnoreCase)
                ? "SOURCE"
                : "TOOL";
        X = 55 + (index * 300);
        Y = 130;
        X = node.PositionX ?? X;
        Y = node.PositionY ?? Y;
        SelectCommand = new RelayCommand(_ => select(this));
        Inputs = new ObservableCollection<PortViewModel>(
            descriptor.Inputs.Select(port =>
                new PortViewModel(NodeId, port, PortDirection.Input, completeInput)));
        Outputs = new ObservableCollection<PortViewModel>(
            descriptor.Outputs.Select(port =>
                new PortViewModel(NodeId, port, PortDirection.Output, startOutput)));
    }

    public string Name { get; }
    public string NodeId { get; }
    public string ToolType { get; }
    public string Category { get; }
    public double X
    {
        get => _x;
        private set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        private set => SetProperty(ref _y, value);
    }
    public ICommand SelectCommand { get; }
    public ObservableCollection<PortViewModel> Inputs { get; }
    public ObservableCollection<PortViewModel> Outputs { get; }

    public void MoveTo(double x, double y)
    {
        X = x;
        Y = y;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        private set => SetProperty(ref _statusColor, value);
    }

    public string Duration
    {
        get => _duration;
        private set
        {
            if (SetProperty(ref _duration, value))
            {
                OnPropertyChanged(nameof(StatusSummary));
            }
        }
    }

    public string StatusSummary => $"{Status} · {Duration}";

    public void Reset()
    {
        Status = "Running";
        StatusColor = "#F0B45B";
        Duration = "--";
    }

    public void Apply(NodeExecutionRecord record)
    {
        Status = record.Succeeded ? "OK" : "FAIL";
        StatusColor = record.Succeeded ? "#45D6A7" : "#E36E7A";
        Duration = $"{record.Duration.TotalMilliseconds:F2} ms";
    }

}

public enum PortDirection
{
    Input,
    Output
}

public sealed class PortViewModel
{
    public PortViewModel(
        string nodeId,
        ToolPortDefinition definition,
        PortDirection direction,
        Action<PortViewModel> activate)
    {
        NodeId = nodeId;
        Name = definition.Name;
        DataType = definition.DataType;
        Direction = direction;
        ActivateCommand = new RelayCommand(_ => activate(this));
    }

    public string NodeId { get; }
    public string Name { get; }
    public DataType DataType { get; }
    public PortDirection Direction { get; }
    public string DisplayName => $"{Name} · {DataType}";
    public string DisplayType => DataType.ToString();
    public ICommand ActivateCommand { get; }
}

public sealed record ProjectState(
    List<WorkflowNodeDefinition> Nodes,
    List<WorkflowEdgeDefinition> Edges);

public sealed record ProjectEdit(ProjectState After, ProjectState Before);

public sealed class WorkflowEdgeViewModel
{
    public WorkflowEdgeViewModel(Point startPoint, Point endPoint)
    {
        StartPoint = startPoint;
        EndPoint = endPoint;
    }

    public Point StartPoint { get; }
    public Point EndPoint { get; }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<bool>? _canExecute;
    private event EventHandler? CanExecuteChangedInternal;

    public RelayCommand(Action<object?> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CanExecuteChangedInternal += value;
        remove => CanExecuteChangedInternal -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() =>
        CanExecuteChangedInternal?.Invoke(this, EventArgs.Empty);
}

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        _ = ExecuteAsync();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteAsync()
    {
        await _execute();
    }
}

public sealed class AsyncCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<bool>? _canExecute;

    public AsyncCommand(Func<T?, Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _ = ExecuteAsync(parameter is T value ? value : default);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteAsync(T? parameter) => await _execute(parameter);
}
