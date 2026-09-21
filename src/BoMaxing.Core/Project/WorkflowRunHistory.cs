using System.Text.Json;

namespace BoMaxing.Core.Project;

public sealed record WorkflowRunHistoryEntry(
    DateTimeOffset OccurredAt,
    string WorkflowId,
    string Status,
    double DurationMilliseconds,
    string Summary);

public sealed record WorkflowRunHistoryQuery(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? WorkflowId = null,
    string? Status = null,
    int Skip = 0,
    int Take = 100)
{
    public void Validate()
    {
        if (From.HasValue && To.HasValue && From > To)
        {
            throw new ArgumentException("History query start must not be after its end.");
        }

        if (Skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Skip));
        }

        if (Take is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(Take));
        }
    }
}

public sealed record WorkflowRunHistoryPage(
    IReadOnlyList<WorkflowRunHistoryEntry> Entries,
    int TotalCount,
    bool HasMore);

public sealed record WorkflowRunTrendPoint(
    DateTimeOffset BucketStart,
    int RunCount,
    int SuccessCount,
    double AverageDurationMilliseconds);

public static class WorkflowRunHistoryQueryService
{
    public static WorkflowRunHistoryPage Query(
        IEnumerable<WorkflowRunHistoryEntry> entries,
        WorkflowRunHistoryQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        query ??= new WorkflowRunHistoryQuery();
        query.Validate();

        var filtered = entries
            .Where(IsValid)
            .Where(entry => !query.From.HasValue || entry.OccurredAt >= query.From)
            .Where(entry => !query.To.HasValue || entry.OccurredAt <= query.To)
            .Where(entry => string.IsNullOrWhiteSpace(query.WorkflowId) ||
                            string.Equals(
                                entry.WorkflowId,
                                query.WorkflowId,
                                StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(query.Status) ||
                            string.Equals(
                                entry.Status,
                                query.Status,
                                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.OccurredAt)
            .ToArray();
        var page = filtered
            .Skip(query.Skip)
            .Take(query.Take)
            .ToArray();
        return new WorkflowRunHistoryPage(
            page,
            filtered.Length,
            query.Skip + page.Length < filtered.Length);
    }

    public static IReadOnlyList<WorkflowRunTrendPoint> BuildTrend(
        IEnumerable<WorkflowRunHistoryEntry> entries,
        TimeSpan bucket,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (bucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(bucket));
        }

        var filtered = entries
            .Where(IsValid)
            .Where(entry => !from.HasValue || entry.OccurredAt >= from)
            .Where(entry => !to.HasValue || entry.OccurredAt <= to)
            .GroupBy(entry => AlignToBucket(entry.OccurredAt, bucket))
            .OrderBy(group => group.Key)
            .Select(group => new WorkflowRunTrendPoint(
                group.Key,
                group.Count(),
                group.Count(entry =>
                    string.Equals(entry.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase)),
                group.Average(entry => entry.DurationMilliseconds)))
            .ToArray();
        return filtered;
    }

    private static DateTimeOffset AlignToBucket(DateTimeOffset value, TimeSpan bucket)
    {
        var utc = value.UtcDateTime;
        var ticks = utc.Ticks - (utc.Ticks % bucket.Ticks);
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private static bool IsValid(WorkflowRunHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.WorkflowId) &&
        !string.IsNullOrWhiteSpace(entry.Status) &&
        !double.IsNaN(entry.DurationMilliseconds) &&
        !double.IsInfinity(entry.DurationMilliseconds) &&
        entry.DurationMilliseconds >= 0;
}

public sealed class WorkflowRunHistoryDocument
{
    public int Version { get; set; } = 1;
    public string ProjectId { get; set; } = string.Empty;
    public List<WorkflowRunHistoryEntry> Entries { get; set; } = [];
}

public sealed class WorkflowRunHistoryStore
{
    private const int CurrentVersion = 1;
    private const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<IReadOnlyList<WorkflowRunHistoryEntry>> LoadAsync(
        string path,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<WorkflowRunHistoryDocument>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("Run history is empty.");
        if (document.Version != CurrentVersion ||
            !string.Equals(document.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Run history version or project ID is invalid.");
        }

        return document.Entries
            .Where(IsValid)
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(MaxEntries)
            .ToArray();
    }

    public async Task SaveAsync(
        string path,
        string projectId,
        IEnumerable<WorkflowRunHistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries
            .Where(IsValid)
            .OrderByDescending(entry => entry.OccurredAt)
            .Take(MaxEntries)
            .ToList();
        var document = new WorkflowRunHistoryDocument
        {
            Version = CurrentVersion,
            ProjectId = projectId,
            Entries = normalized
        };
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsValid(WorkflowRunHistoryEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.WorkflowId) &&
        !string.IsNullOrWhiteSpace(entry.Status) &&
        !double.IsNaN(entry.DurationMilliseconds) &&
        !double.IsInfinity(entry.DurationMilliseconds) &&
        entry.DurationMilliseconds >= 0;
}
