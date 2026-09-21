namespace BoMaxing.Core.Project;

public enum DeploymentServicePlatform
{
    Windows,
    Systemd
}

public sealed record DeploymentServiceDefinition(
    string ServiceName,
    string DisplayName,
    string Description,
    string ExecutablePath,
    string Arguments = "",
    string? WorkingDirectory = null,
    bool RestartOnFailure = true);

public sealed record DeploymentServiceFile(
    string FileName,
    string Content);

public static class DeploymentServiceFileGenerator
{
    public static DeploymentServiceFile Generate(
        DeploymentServiceDefinition definition,
        DeploymentServicePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateName(definition.ServiceName);
        return platform switch
        {
            DeploymentServicePlatform.Windows => GenerateWindows(definition),
            DeploymentServicePlatform.Systemd => GenerateSystemd(definition),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
    }

    private static DeploymentServiceFile GenerateWindows(
        DeploymentServiceDefinition definition)
    {
        var executable = PowerShellQuote(definition.ExecutablePath);
        var arguments = PowerShellQuote(definition.Arguments);
        var displayName = PowerShellQuote(definition.DisplayName);
        var description = PowerShellQuote(definition.Description);
        var workingDirectory = definition.WorkingDirectory is null
            ? string.Empty
            : $"\n$workingDirectory = {PowerShellQuote(definition.WorkingDirectory)}";
        var workingDirectoryOption = definition.WorkingDirectory is null
            ? string.Empty
            : ",`n    -WorkingDirectory $workingDirectory";
        var failureAction = definition.RestartOnFailure
            ? "\nsc.exe failure $serviceName actions= restart/60000/restart/60000/\"\"/60000 reset= 86400"
            : string.Empty;
        var content = string.Join(
            Environment.NewLine,
            "$ErrorActionPreference = 'Stop'",
            $"$serviceName = {PowerShellQuote(definition.ServiceName)}",
            $"$displayName = {displayName}",
            $"$description = {description}",
            $"$binaryPath = \"`\"{executable}`\" `\"{arguments}`\"\"{workingDirectory}",
            "if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {",
            "    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue",
            "    sc.exe delete $serviceName | Out-Null",
            "}",
            $"New-Service -Name $serviceName -DisplayName $displayName -Description $description -BinaryPathName $binaryPath -StartupType Automatic{workingDirectoryOption}",
            failureAction.TrimStart(),
            "Write-Host \"Installed service $serviceName\"");
        return new DeploymentServiceFile("install-service.ps1", content);
    }

    private static DeploymentServiceFile GenerateSystemd(
        DeploymentServiceDefinition definition)
    {
        var workingDirectory = definition.WorkingDirectory ?? "/opt/bomaxing";
        var restart = definition.RestartOnFailure ? "on-failure" : "no";
        var arguments = string.IsNullOrWhiteSpace(definition.Arguments)
            ? string.Empty
            : " " + definition.Arguments;
        var content = string.Join(
            Environment.NewLine,
            "[Unit]",
            $"Description={SystemdValue(definition.Description)}",
            "After=network-online.target",
            "Wants=network-online.target",
            string.Empty,
            "[Service]",
            "Type=simple",
            $"WorkingDirectory={SystemdValue(workingDirectory)}",
            $"ExecStart={SystemdValue(definition.ExecutablePath)}{arguments}",
            $"Restart={restart}",
            "RestartSec=5",
            "KillSignal=SIGINT",
            "TimeoutStopSec=30",
            string.Empty,
            "[Install]",
            "WantedBy=multi-user.target");
        return new DeploymentServiceFile($"{definition.ServiceName}.service", content);
    }

    private static string PowerShellQuote(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string SystemdValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(character => char.IsWhiteSpace(character) ||
                                 character is '/' or '\\' or ':' or '"'))
        {
            throw new ArgumentException(
                "Service name cannot contain whitespace or path separator characters.",
                nameof(name));
        }
    }
}

public static class DeploymentServiceFileWriter
{
    public static async Task<string> WriteAsync(
        DeploymentServiceFile file,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (Path.GetFileName(file.FileName) != file.FileName)
        {
            throw new InvalidDataException("Service file name must not contain a directory.");
        }

        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file.FileName);
        await File.WriteAllTextAsync(path, file.Content, cancellationToken);
        return path;
    }
}
