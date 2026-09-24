using System.Net;
using System.Text.Json;
using iOneDataGrove.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class GitHubBranchCommitImporter(IOneDataGroveDbContext db, string token)
{
    public async Task ImportAsync(long repositoryId, bool forceFull, CancellationToken ct)
    {
        var repository = await db.Repositories.SingleAsync(r => r.Id == repositoryId, ct);
        if (repository.IsExcluded || !repository.IsSyncEnabled)
            throw new InvalidOperationException("Repository escluso o sincronizzazione sospesa.");
        var tracker = new IngestionSyncTracker(db);
        var execution = await tracker.StartAsync(repositoryId, "commits", forceFull, ct);
        try
        {
            using var github = new GitHubApiClient(token);
            var path = "repos/" + string.Join('/', repository.FullName.Split('/').Select(Uri.EscapeDataString));
            using var metadata = JsonDocument.Parse(await github.GetAsync(path, ct));
            var branch = metadata.RootElement.GetProperty("default_branch").GetString();
            var previous = ReadCursor(execution.State.Cursor);
            string? head = null;
            if (!string.IsNullOrWhiteSpace(branch))
            {
                try
                {
                    using var tip = JsonDocument.Parse(await github.GetAsync(
                        $"{path}/commits?sha={Uri.EscapeDataString(branch)}&per_page=1", ct));
                    if (tip.RootElement.GetArrayLength() > 0)
                        head = tip.RootElement[0].GetProperty("sha").GetString();
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // GitHub responds 409 when an empty repository has no history yet.
                }
            }

            IReadOnlyList<string> shas = [];
            var mode = head is null ? "empty" : "unchanged";
            if (head is not null && (forceFull || previous?.Head != head || previous.Branch != branch))
            {
                IReadOnlyList<string>? delta = null;
                if (!forceFull && previous?.Head is { } oldHead && previous.Branch == branch)
                    delta = await github.GetComparedCommitShasAsync(path, oldHead, head, ct);
                if (delta is not null)
                {
                    mode = "incremental";
                    shas = delta;
                }
                else
                {
                    mode = "backfill";
                    // Pin every page to the same head; do not filter by commit dates or stop at a known PR commit.
                    var history = await github.GetAllPagesAsync(
                        page => $"{path}/commits?sha={head}&per_page=100&page={page}", ct);
                    shas = history.Select(raw => {
                        using var item = JsonDocument.Parse(raw);
                        return item.RootElement.GetProperty("sha").GetString()!;
                    }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }

            var known = await db.Commits.AsNoTracking().Where(c => c.RepositoryId == repositoryId)
                .Select(c => new { c.Sha, c.Id, c.FilesChanged, FileCount = c.CommitFiles.Count })
                .ToDictionaryAsync(c => c.Sha, StringComparer.OrdinalIgnoreCase, ct);
            var recovery = new GitHubCommitRecovery(db, token);
            var inserted = 0;
            var repaired = 0;
            var processed = 0;
            var addedLinks = 0;
            var indexer = new KnowledgeLinkIndexer(db);
            foreach (var batch in shas.Chunk(25))
            {
                ct.ThrowIfCancellationRequested();
                var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var missing = batch.Where(sha => !known.TryGetValue(sha, out var c) || !IsComplete(c.FilesChanged, c.FileCount));
                // Only independent HTTP reads run concurrently; the DbContext stays on one sequential path.
                foreach (var group in missing.Chunk(4))
                {
                    var fetched = await Task.WhenAll(group.Select(async sha =>
                        (Sha: sha, Raw: await github.GetCommitWithAllFilesAsync($"{path}/commits/{sha}", ct))));
                    foreach (var item in fetched) details.Add(item.Sha, item.Raw);
                }
                var ids = new List<long>();
                foreach (var sha in batch)
                {
                    if (details.TryGetValue(sha, out var raw))
                    {
                        ids.Add(await recovery.SaveAsync(repository, sha, raw, ct));
                        if (known.ContainsKey(sha)) repaired++; else inserted++;
                    }
                    else ids.Add(known[sha].Id);
                }
                addedLinks += await indexer.IndexCommitsAsync(repositoryId, repository.FullName, ids.ToArray(), ct);
                processed += batch.Length;
                Console.WriteLine($"Commit branch {branch}: {processed}/{shas.Count}, nuovi={inserted}, riparati={repaired}.");
            }

            await tracker.CompleteAsync(execution, new SyncMetrics(shas.Count, inserted, repaired,
                JsonSerializer.Serialize(new { branch, head, mode, reused = shas.Count - inserted - repaired, addedLinks }),
                JsonSerializer.Serialize(new BranchCommitCursor(branch, head))), ct);
            Console.WriteLine($"Commit {repository.FullName}: modalità={mode}, esaminati={shas.Count}, nuovi={inserted}, riparati={repaired}, collegamenti aggiunti={addedLinks}.");
        }
        catch (Exception ex)
        {
            await tracker.FailAsync(execution, ex, CancellationToken.None);
            throw;
        }
    }

    internal static bool IsComplete(int? expectedFiles, int actualFiles) =>
        expectedFiles.HasValue && expectedFiles.Value == actualFiles;

    internal static BranchCommitCursor? ReadCursor(string? raw)
    {
        if (raw is null) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<BranchCommitCursor>(raw);
            if (cursor?.Head is { } head) GitHubCommitRecovery.ValidateSha(head);
            return cursor;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return null; }
    }
}

internal sealed record BranchCommitCursor(string? Branch, string? Head);
