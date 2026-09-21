using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoMaxing.Core.Project;

public sealed class DeploymentManifest
{
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<DeploymentFile> Files { get; set; } = [];
}

public sealed record DeploymentFile(string Path, long Length, string Sha256);

public sealed class DeploymentPackageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<DeploymentManifest> WriteAsync(
        ProjectDocument project,
        string packagePath,
        IReadOnlyDictionary<string, byte[]>? additionalFiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        additionalFiles ??= new Dictionary<string, byte[]>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(packagePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var projectBytes = JsonSerializer.SerializeToUtf8Bytes(project, JsonOptions);
        var files = new List<(string Path, byte[] Data)> { ("project.bomaxing.json", projectBytes) };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "project.bomaxing.json",
            "deployment.manifest.json"
        };
        foreach (var file in additionalFiles)
        {
            var path = NormalizePath(file.Key);
            if (!paths.Add(path))
            {
                throw new InvalidDataException(
                    $"Deployment package contains duplicate file '{path}'.");
            }

            files.Add((path, file.Value ?? throw new InvalidDataException(
                $"Deployment file '{file.Key}' has no data.")));
        }
        var manifest = new DeploymentManifest
        {
            ProjectId = project.Id,
            ProjectName = project.Name,
            Files = files.Select(file => new DeploymentFile(
                file.Path,
                file.Data.LongLength,
                Convert.ToHexString(SHA256.HashData(file.Data)))).ToList()
        };

        await using var output = File.Create(packagePath);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, "deployment.manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        foreach (var file in files)
        {
            WriteEntry(archive, file.Path, file.Data);
        }

        await output.FlushAsync(cancellationToken);
        return manifest;
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] data)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
        stream.Write(data);
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Equals("..", StringComparison.Ordinal) ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.Contains("/../", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("Deployment file path cannot escape the package root.");
        }

        return normalized;
    }
}
