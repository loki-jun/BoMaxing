using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoMaxing.Core.Project;

public sealed record DeploymentVerificationResult(
    DeploymentManifest Manifest,
    IReadOnlyList<string> Files);

public sealed class DeploymentPackageVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DeploymentVerificationResult> VerifyAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        await using var input = File.OpenRead(packagePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("deployment.manifest.json")
            ?? throw new InvalidDataException("Deployment package has no manifest.");
        DeploymentManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<DeploymentManifest>(
                           manifestStream,
                           JsonOptions,
                           cancellationToken)
                       ?? throw new InvalidDataException("Deployment manifest is empty.");
        }

        if (manifest.Version != 1 || string.IsNullOrWhiteSpace(manifest.ProjectId))
        {
            throw new InvalidDataException("Deployment manifest version or project ID is invalid.");
        }

        var expected = new Dictionary<string, DeploymentFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var path = NormalizePath(file.Path);
            if (!expected.TryAdd(path, file with { Path = path }))
            {
                throw new InvalidDataException($"Deployment manifest contains duplicate file '{path}'.");
            }
        }

        var actualEntries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? projectId = null;
        foreach (var entry in actualEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = NormalizePath(entry.FullName);
            if (!actualPaths.Add(path))
            {
                throw new InvalidDataException($"Deployment package contains duplicate file '{path}'.");
            }

            if (path.Equals("deployment.manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!expected.TryGetValue(path, out var expectedFile))
            {
                throw new InvalidDataException($"Deployment package contains unlisted file '{path}'.");
            }

            if (entry.Length != expectedFile.Length)
            {
                throw new InvalidDataException($"Deployment file '{path}' length does not match the manifest.");
            }

            await using var stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            var data = buffer.ToArray();
            if (path.Equals("project.bomaxing.json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(data);
                if (!document.RootElement.TryGetProperty("id", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(idProperty.GetString()))
                {
                    throw new InvalidDataException("Deployment project has no valid ID.");
                }

                projectId = idProperty.GetString();
                if (!string.Equals(projectId, manifest.ProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Deployment project ID does not match the manifest.");
                }

            }

            var hash = SHA256.HashData(data);
            byte[] expectedHash;
            try
            {
                expectedHash = Convert.FromHexString(expectedFile.Sha256);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    $"Deployment file '{path}' has an invalid SHA-256 value.",
                    exception);
            }

            if (!hash.AsSpan().SequenceEqual(expectedHash))
            {
                throw new InvalidDataException($"Deployment file '{path}' hash does not match the manifest.");
            }
        }

        if (projectId is null ||
            actualPaths.Count != expected.Count + 1 ||
            expected.Keys.Any(path => !actualPaths.Contains(path)))
        {
            throw new InvalidDataException("Deployment package contents do not match the manifest.");
        }

        return new DeploymentVerificationResult(manifest, expected.Keys.ToArray());
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0 ||
            normalized.Equals(".", StringComparison.Ordinal) ||
            normalized.Equals("..", StringComparison.Ordinal) ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.Contains("/../", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("Deployment file path cannot escape the package root.");
        }

        return normalized;
    }
}

public sealed record DeploymentState(
    string ProjectId,
    string CurrentRelease,
    string? PreviousRelease,
    DateTimeOffset InstalledAt);

public sealed record DeploymentHealthReport(
    bool IsHealthy,
    string? ProjectId,
    string? ProjectName,
    string? CurrentRelease,
    string? ReleasePath,
    IReadOnlyList<string> Issues);

public sealed class DeploymentPackageInstaller
{
    private const string PointerFileName = "current.release";
    private const string StateFileName = "deployment.state.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly DeploymentPackageVerifier _verifier = new();

    public async Task<DeploymentState> InstallAsync(
        string packagePath,
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var verification = await _verifier.VerifyAsync(packagePath, cancellationToken);
        var root = GetRoot(installRoot);
        var releases = Path.Combine(root, "releases");
        var staging = Path.Combine(root, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releases);
        Directory.CreateDirectory(staging);

        try
        {
            await ExtractVerifiedPackageAsync(packagePath, staging, verification, cancellationToken);
            var releaseName = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var releasePath = Path.Combine(releases, releaseName);
            Directory.Move(staging, releasePath);
            staging = string.Empty;
            var health = await CheckReleaseHealthAsync(
                releasePath,
                verification.Manifest.ProjectId,
                cancellationToken);
            if (!health.IsHealthy)
            {
                Directory.Delete(releasePath, recursive: true);
                throw new InvalidDataException(
                    $"Deployment release health check failed: {string.Join("; ", health.Issues)}");
            }

            var previous = await ReadStateAsync(root, cancellationToken);
            var state = new DeploymentState(
                verification.Manifest.ProjectId,
                releaseName,
                previous?.CurrentRelease,
                DateTimeOffset.UtcNow);
            await WriteStateAsync(root, state, cancellationToken);
            return state;
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public async Task<DeploymentState> RollbackAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var root = GetRoot(installRoot);
        var current = await ReadStateAsync(root, cancellationToken)
            ?? throw new InvalidOperationException("No deployment state is available for rollback.");
        if (string.IsNullOrWhiteSpace(current.PreviousRelease))
        {
            throw new InvalidOperationException("No previous deployment release is available.");
        }

        var previousPath = GetReleasePath(root, current.PreviousRelease);
        if (!Directory.Exists(previousPath))
        {
            throw new InvalidDataException("The previous deployment release is missing.");
        }

        var next = current with
        {
            CurrentRelease = current.PreviousRelease,
            PreviousRelease = current.CurrentRelease,
            InstalledAt = DateTimeOffset.UtcNow
        };
        await WriteStateAsync(root, next, cancellationToken);
        return next;
    }

    public async Task<string> GetCurrentReleasePathAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var root = GetRoot(installRoot);
        var state = await ReadStateAsync(root, cancellationToken)
            ?? throw new InvalidOperationException("No deployment has been installed.");
        var path = GetReleasePath(root, state.CurrentRelease);
        if (!Directory.Exists(path))
        {
            throw new InvalidDataException("The current deployment release is missing.");
        }

        return path;
    }

    public async Task<DeploymentHealthReport> CheckHealthAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var root = GetRoot(installRoot);
        var issues = new List<string>();
        DeploymentState? state;
        try
        {
            state = await ReadStateAsync(root, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new DeploymentHealthReport(
                false,
                null,
                null,
                null,
                null,
                [$"Deployment state cannot be read: {exception.Message}"]);
        }

        if (state is null)
        {
            return new DeploymentHealthReport(
                false,
                null,
                null,
                null,
                null,
                ["No deployment state is available."]);
        }

        string? releasePath = null;
        string? projectId = null;
        string? projectName = null;
        try
        {
            releasePath = GetReleasePath(root, state.CurrentRelease);
            if (!Directory.Exists(releasePath))
            {
                issues.Add("The current deployment release is missing.");
            }
            else
            {
                var projectPath = Path.Combine(releasePath, "project.bomaxing.json");
                if (!File.Exists(projectPath))
                {
                    issues.Add("The current release has no project file.");
                }
                else
                {
                    var project = await new ProjectFileStore().LoadAsync(
                        projectPath,
                        cancellationToken);
                    projectId = project.Id;
                    projectName = project.Name;
                    if (!string.Equals(project.Id, state.ProjectId, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add("The deployed project ID does not match deployment state.");
                    }

                    if (project.Workflows.Count == 0)
                    {
                        issues.Add("The deployed project has no workflow.");
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException)
        {
            issues.Add($"The current release is not loadable: {exception.Message}");
        }

        return new DeploymentHealthReport(
            issues.Count == 0,
            projectId,
            projectName,
            state.CurrentRelease,
            releasePath,
            issues);
    }

    public async Task<int> CleanupReleasesAsync(
        string installRoot,
        int keepReleases = 2,
        CancellationToken cancellationToken = default)
    {
        if (keepReleases <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepReleases));
        }

        var root = GetRoot(installRoot);
        var releases = Path.Combine(root, "releases");
        if (!Directory.Exists(releases))
        {
            return 0;
        }

        var state = await ReadStateAsync(root, cancellationToken);
        var protectedReleases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (state is not null)
        {
            protectedReleases.Add(state.CurrentRelease);
            if (!string.IsNullOrWhiteSpace(state.PreviousRelease))
            {
                protectedReleases.Add(state.PreviousRelease);
            }
        }

        var candidates = new DirectoryInfo(releases)
            .EnumerateDirectories()
            .OrderByDescending(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var retained = new HashSet<string>(
            candidates.Take(keepReleases).Select(directory => directory.Name),
            StringComparer.OrdinalIgnoreCase);
        retained.UnionWith(protectedReleases);
        var removed = 0;
        foreach (var directory in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retained.Contains(directory.Name))
            {
                continue;
            }

            directory.Delete(recursive: true);
            removed++;
        }

        return removed;
    }

    private static async Task ExtractVerifiedPackageAsync(
        string packagePath,
        string staging,
        DeploymentVerificationResult verification,
        CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(packagePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        foreach (var path in verification.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(path)
                ?? throw new InvalidDataException($"Verified deployment file '{path}' is missing.");
            var destination = GetSafePath(staging, path);
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var source = entry.Open();
            await using var target = File.Create(destination);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private static async Task<DeploymentHealthReport> CheckReleaseHealthAsync(
        string releasePath,
        string expectedProjectId,
        CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var projectPath = Path.Combine(releasePath, "project.bomaxing.json");
        string? projectId = null;
        string? projectName = null;
        if (!File.Exists(projectPath))
        {
            issues.Add("The release has no project file.");
        }
        else
        {
            try
            {
                var project = await new ProjectFileStore().LoadAsync(
                    projectPath,
                    cancellationToken);
                projectId = project.Id;
                projectName = project.Name;
                if (!string.Equals(project.Id, expectedProjectId, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add("The release project ID does not match the package manifest.");
                }

                if (project.Workflows.Count == 0)
                {
                    issues.Add("The release project has no workflow.");
                }
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidDataException)
            {
                issues.Add($"The release project is not loadable: {exception.Message}");
            }
        }

        return new DeploymentHealthReport(
            issues.Count == 0,
            projectId,
            projectName,
            null,
            releasePath,
            issues);
    }

    private static async Task<DeploymentState?> ReadStateAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, StateFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DeploymentState>(
            stream,
            JsonOptions,
            cancellationToken);
    }

    private static async Task WriteStateAsync(
        string root,
        DeploymentState state,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, StateFileName);
        var tempPath = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, statePath, overwrite: true);
        var pointerPath = Path.Combine(root, PointerFileName);
        var pointerTemp = pointerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(pointerTemp, state.CurrentRelease, cancellationToken);
        File.Move(pointerTemp, pointerPath, overwrite: true);
    }

    private static string GetRoot(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        return Path.GetFullPath(installRoot);
    }

    private static string GetReleasePath(string root, string release)
    {
        var normalized = DeploymentPackageVerifier.NormalizePath(release);
        if (normalized.Contains('/'))
        {
            throw new InvalidDataException("Deployment release name is invalid.");
        }

        return GetSafePath(Path.Combine(root, "releases"), normalized);
    }

    private static string GetSafePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Deployment path escapes the target directory.");
        }

        return fullPath;
    }
}
