using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoMaxing.Core.Project;

public sealed class ProjectFileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(
        ProjectDocument project,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        project.Touch();
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, project, SerializerOptions, cancellationToken);
    }

    public async Task<ProjectDocument> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = File.OpenRead(filePath);
        var project = await JsonSerializer.DeserializeAsync<ProjectDocument>(
            stream,
            SerializerOptions,
            cancellationToken);

        return ProjectSchemaMigrator.Migrate(
            project ?? throw new InvalidDataException("Project file is empty."));
    }
}
