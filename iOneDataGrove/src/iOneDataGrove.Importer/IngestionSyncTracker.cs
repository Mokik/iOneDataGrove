using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class IngestionSyncTracker(IOneDataGroveDbContext dbContext)
{
    private static readonly TimeSpan IncrementalOverlap = TimeSpan.FromMinutes(5);

    public async Task<int> RecoverInterruptedRunsAsync(
        CancellationToken cancellationToken = default)
    {
        var recoveredAt = DateTime.UtcNow;
        const string interruptionMessage =
            "Esecuzione precedente interrotta prima del completamento.";

        var recoveredRuns = await dbContext.SyncRuns
            .Where(item => item.Status == "running")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.CompletedAt, recoveredAt)
                .SetProperty(item => item.ItemsFailed, 1)
                .SetProperty(item => item.ErrorMessage, interruptionMessage),
                cancellationToken);

        await dbContext.SyncStates
            .Where(item => item.Status == "running")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.UpdatedAt, recoveredAt),
                cancellationToken);

        return recoveredRuns;
    }

    public async Task<SyncExecution> StartAsync(
        long repositoryId,
        string resourceType,
        bool forceFull,
        CancellationToken cancellationToken = default)
    {
        var state = await dbContext.SyncStates.SingleOrDefaultAsync(
            item => item.RepositoryId == repositoryId &&
                    item.ResourceType == resourceType,
            cancellationToken);

        if (state is null)
        {
            var existingSyncPoint = await GetExistingSyncPointAsync(
                repositoryId,
                resourceType,
                cancellationToken);
            state = new SyncState
            {
                RepositoryId = repositoryId,
                ResourceType = resourceType,
                Status = "pending",
                UpdatedAt = DateTime.UtcNow,
                LastSuccessfulSync = existingSyncPoint
            };
            dbContext.SyncStates.Add(state);
        }

        var since = forceFull
            ? null
            : state.LastSuccessfulSync?.Subtract(IncrementalOverlap);
        var syncType = since.HasValue ? "incremental" : "backfill";
        var startedAt = DateTime.UtcNow;
        var run = new SyncRun
        {
            RepositoryId = repositoryId,
            ResourceType = resourceType,
            SyncType = syncType,
            StartedAt = startedAt,
            Status = "running"
        };

        state.Status = "running";
        state.UpdatedAt = startedAt;

        if (syncType == "backfill")
        {
            state.BackfillFrom ??= startedAt;
        }

        dbContext.SyncRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new SyncExecution(state, run, since, syncType);
    }

    private async Task<DateTime?> GetExistingSyncPointAsync(
        long repositoryId,
        string resourceType,
        CancellationToken cancellationToken)
    {
        if (resourceType == "issues")
        {
            var issueSync = await dbContext.Issues
                .Where(item => item.RepositoryId == repositoryId)
                .Select(item => (DateTime?)item.SyncedAt)
                .MaxAsync(cancellationToken);
            var commentSync = await dbContext.IssueComments
                .Where(item => item.RepositoryId == repositoryId)
                .Select(item => (DateTime?)item.SyncedAt)
                .MaxAsync(cancellationToken);

            return Max(issueSync, commentSync);
        }

        if (resourceType == "pull_requests")
        {
            var pullRequestSync = await dbContext.PullRequests
                .Where(item => item.RepositoryId == repositoryId)
                .Select(item => (DateTime?)item.SyncedAt)
                .MaxAsync(cancellationToken);
            var commitSync = await dbContext.Commits
                .Where(item => item.RepositoryId == repositoryId)
                .Select(item => (DateTime?)item.SyncedAt)
                .MaxAsync(cancellationToken);

            return Max(pullRequestSync, commitSync);
        }

        if (resourceType == "repository_files")
        {
            return await dbContext.RepositoryFiles
                .Where(item => item.RepositoryId == repositoryId)
                .Select(item => (DateTime?)item.SyncedAt)
                .MaxAsync(cancellationToken);
        }

        return null;
    }

    private static DateTime? Max(DateTime? first, DateTime? second) =>
        first is null
            ? second
            : second is null || first >= second
                ? first
                : second;

    public async Task CompleteAsync(
        SyncExecution execution,
        SyncMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        var completedAt = DateTime.UtcNow;
        execution.Run.CompletedAt = completedAt;
        execution.Run.Status = "completed";
        execution.Run.ItemsRead = metrics.ItemsRead;
        execution.Run.ItemsInserted = metrics.ItemsInserted;
        execution.Run.ItemsUpdated = metrics.ItemsUpdated;
        execution.Run.ItemsFailed = 0;
        execution.Run.Metadata = metrics.Metadata;

        // The next incremental read must restart from when this run began, not
        // from when it ended. Otherwise updates made while a long run is in
        // progress can fall outside the overlap window and be lost.
        execution.State.LastSuccessfulSync = GetIncrementalWatermark(execution);
        execution.State.Status = "completed";
        execution.State.UpdatedAt = completedAt;
        execution.State.Cursor = metrics.Cursor;

        if (execution.SyncType == "backfill")
        {
            execution.State.BackfillTo = completedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static DateTime GetIncrementalWatermark(SyncExecution execution) =>
        execution.Run.StartedAt;

    public async Task FailAsync(
        SyncExecution execution,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        var runId = execution.Run.Id;
        var stateId = execution.State.Id;
        dbContext.ChangeTracker.Clear();

        var run = await dbContext.SyncRuns.SingleAsync(
            item => item.Id == runId,
            cancellationToken);
        var state = await dbContext.SyncStates.SingleAsync(
            item => item.Id == stateId,
            cancellationToken);
        var completedAt = DateTime.UtcNow;
        run.CompletedAt = completedAt;
        run.Status = "failed";
        run.ItemsFailed = 1;
        run.ErrorMessage = FormatException(exception);
        state.Status = "failed";
        state.UpdatedAt = completedAt;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(current.Message, StringComparer.Ordinal))
                messages.Add(current.Message);
        return string.Join(" | Causa: ", messages);
    }
}

internal sealed record SyncExecution(
    SyncState State,
    SyncRun Run,
    DateTime? Since,
    string SyncType);

internal sealed record SyncMetrics(
    int ItemsRead,
    int ItemsInserted,
    int ItemsUpdated,
    string? Metadata = null,
    string? Cursor = null);
