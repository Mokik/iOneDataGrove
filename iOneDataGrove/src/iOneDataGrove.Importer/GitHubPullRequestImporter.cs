using System.Text.Json;
using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class GitHubPullRequestImporter(
    IOneDataGroveDbContext dbContext,
    string token)
{
    public async Task<PullRequestImportResult> ImportAsync(
        long repositoryId,
        string owner,
        string repositoryName,
        DateTime? since = null,
        CancellationToken cancellationToken = default)
    {
        using var github = new GitHubApiClient(token);
        var escapedOwner = Uri.EscapeDataString(owner);
        var escapedRepository = Uri.EscapeDataString(repositoryName);
        Func<int, string> pullRequestsPage = page =>
            $"repos/{escapedOwner}/{escapedRepository}/pulls" +
            $"?state=all&sort=updated&direction=desc&per_page=100&page={page}";
        var rawPullRequests = since.HasValue
            ? await github.GetDescendingPagesWhileAsync(
                pullRequestsPage,
                raw => IsUpdatedSince(raw, since.Value),
                cancellationToken)
            : await github.GetAllPagesAsync(pullRequestsPage, cancellationToken);

        var repository = await dbContext.Repositories.SingleAsync(
            item => item.Id == repositoryId,
            cancellationToken);
        var users = await dbContext.Users.ToDictionaryAsync(
            user => user.GithubId,
            cancellationToken);
        var pullRequests = await dbContext.PullRequests
            .Where(pullRequest => pullRequest.RepositoryId == repositoryId)
            .ToDictionaryAsync(pullRequest => pullRequest.GithubId, cancellationToken);
        var commits = await dbContext.Commits
            .Where(commit => commit.RepositoryId == repositoryId)
            .ToDictionaryAsync(commit => commit.Sha, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var result = new MutablePullRequestImportResult(rawPullRequests.Count);
        var processed = 0;

        foreach (var rawSummary in rawPullRequests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var summaryDocument = JsonDocument.Parse(rawSummary);
            var number = summaryDocument.RootElement.GetProperty("number").GetInt32();
            var pullBasePath =
                $"repos/{escapedOwner}/{escapedRepository}/pulls/{number}";

            var rawDetail = await github.GetAsync(pullBasePath, cancellationToken);
            var rawFiles = await github.GetPullRequestFilesAsync(
                pullBasePath,
                rawDetail,
                cancellationToken);
            var rawPullRequestCommits = await github.GetAllPagesAsync(
                page => $"{pullBasePath}/commits?per_page=100&page={page}",
                cancellationToken);

            ValidateCompleteness(
                number,
                rawDetail,
                rawFiles.Count,
                rawPullRequestCommits.Count);

            var missingCommitDetails = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var rawCommit in rawPullRequestCommits)
            {
                using var commitDocument = JsonDocument.Parse(rawCommit);
                var sha = commitDocument.RootElement.GetProperty("sha").GetString()!;

                if (missingCommitDetails.ContainsKey(sha))
                {
                    continue;
                }

                if (commits.TryGetValue(sha, out var existingCommit) &&
                    existingCommit.FilesChanged is not >= 300)
                {
                    continue;
                }

                missingCommitDetails.Add(
                    sha,
                    await github.GetCommitWithAllFilesAsync(
                        $"repos/{escapedOwner}/{escapedRepository}/commits/{sha}",
                        cancellationToken));
            }

            await using var transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            using var detailDocument = JsonDocument.Parse(rawDetail);
            var root = detailDocument.RootElement;
            var githubId = root.GetProperty("id").GetInt64();
            var inserted = !pullRequests.TryGetValue(githubId, out var pullRequest);
            pullRequest ??= new PullRequest { GithubId = githubId };
            var syncedAt = DateTime.UtcNow;

            pullRequest.Repository = repository;
            pullRequest.NodeId = GetOptionalString(root, "node_id");
            pullRequest.Number = root.GetProperty("number").GetInt32();
            pullRequest.AuthorUser = UpsertUser(
                root,
                "user",
                users,
                syncedAt,
                result);
            pullRequest.Title = root.GetProperty("title").GetString()!;
            pullRequest.Body = GetOptionalString(root, "body");
            pullRequest.State = root.GetProperty("state").GetString()!;
            pullRequest.IsDraft = root.GetProperty("draft").GetBoolean();
            pullRequest.Locked = root.GetProperty("locked").GetBoolean();

            var baseJson = root.GetProperty("base");
            var headJson = root.GetProperty("head");
            pullRequest.BaseBranch = GetOptionalString(baseJson, "ref");
            pullRequest.BaseSha = GetOptionalString(baseJson, "sha");
            pullRequest.HeadBranch = GetOptionalString(headJson, "ref");
            pullRequest.HeadSha = GetOptionalString(headJson, "sha");
            pullRequest.Merged = root.GetProperty("merged").GetBoolean();
            pullRequest.MergedAt = GetOptionalUtcDateTime(root, "merged_at");
            pullRequest.MergedByUser = UpsertUser(
                root,
                "merged_by",
                users,
                syncedAt,
                result);
            pullRequest.MergeCommitSha = GetOptionalString(root, "merge_commit_sha");
            pullRequest.CommitsCount = GetOptionalInt32(root, "commits");
            pullRequest.Additions = GetOptionalInt32(root, "additions");
            pullRequest.Deletions = GetOptionalInt32(root, "deletions");
            pullRequest.ChangedFiles = GetOptionalInt32(root, "changed_files");
            pullRequest.HtmlUrl = GetOptionalString(root, "html_url");
            pullRequest.CreatedAt = GetRequiredUtcDateTime(root, "created_at");
            pullRequest.UpdatedAt = GetRequiredUtcDateTime(root, "updated_at");
            pullRequest.ClosedAt = GetOptionalUtcDateTime(root, "closed_at");
            pullRequest.SyncedAt = syncedAt;
            pullRequest.RawJson = rawDetail;

            if (inserted)
            {
                dbContext.PullRequests.Add(pullRequest);
                pullRequests.Add(githubId, pullRequest);
                result.PullRequestsInserted++;
            }
            else
            {
                result.PullRequestsUpdated++;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            var existingPullRequestFiles = await dbContext.PullRequestFiles
                .Where(file => file.PullRequestId == pullRequest.Id)
                .ToDictionaryAsync(file => file.Filename, StringComparer.Ordinal, cancellationToken);
            var remoteFilenames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawFile in rawFiles)
            {
                using var fileDocument = JsonDocument.Parse(rawFile);
                var fileJson = fileDocument.RootElement;
                var filename = fileJson.GetProperty("filename").GetString()!;
                remoteFilenames.Add(filename);
                var fileInserted = !existingPullRequestFiles.TryGetValue(filename, out var file);
                file ??= new PullRequestFile { Filename = filename };

                file.PullRequest = pullRequest;
                file.PreviousFilename = GetOptionalString(fileJson, "previous_filename");
                file.Status = GetOptionalString(fileJson, "status");
                file.Additions = GetOptionalInt32(fileJson, "additions");
                file.Deletions = GetOptionalInt32(fileJson, "deletions");
                file.Changes = GetOptionalInt32(fileJson, "changes");
                file.Patch = GetOptionalString(fileJson, "patch");
                file.RawJson = rawFile;

                if (fileInserted)
                {
                    dbContext.PullRequestFiles.Add(file);
                    result.PullRequestFilesInserted++;
                }
                else
                {
                    result.PullRequestFilesUpdated++;
                }
            }

            var stalePullRequestFiles = existingPullRequestFiles.Values
                .Where(file => !remoteFilenames.Contains(file.Filename))
                .ToList();
            dbContext.PullRequestFiles.RemoveRange(stalePullRequestFiles);
            result.PullRequestFilesRemoved += stalePullRequestFiles.Count;

            foreach (var pair in missingCommitDetails)
            {
                using var commitDocument = JsonDocument.Parse(pair.Value);
                var commitJson = commitDocument.RootElement;
                var gitCommitJson = commitJson.GetProperty("commit");
                var authorJson = gitCommitJson.GetProperty("author");
                var committerJson = gitCommitJson.GetProperty("committer");
                var commitInserted = !commits.TryGetValue(pair.Key, out var commit);
                commit ??= new Commit { Sha = pair.Key };
                commit.Repository = repository;
                commit.AuthorUser = UpsertUser(
                    commitJson,
                    "author",
                    users,
                    syncedAt,
                    result);
                commit.CommitterUser = UpsertUser(
                    commitJson,
                    "committer",
                    users,
                    syncedAt,
                    result);
                commit.AuthorName = GetOptionalString(authorJson, "name");
                commit.AuthorEmail = GetOptionalString(authorJson, "email");
                commit.CommitterName = GetOptionalString(committerJson, "name");
                commit.CommitterEmail = GetOptionalString(committerJson, "email");
                commit.Message = gitCommitJson.GetProperty("message").GetString()!;
                commit.AuthoredAt = GetOptionalUtcDateTime(authorJson, "date");
                commit.CommittedAt = GetOptionalUtcDateTime(committerJson, "date");
                commit.HtmlUrl = GetOptionalString(commitJson, "html_url");
                commit.SyncedAt = syncedAt;
                commit.RawJson = pair.Value;

                if (!commitInserted)
                {
                    var existingCommitFiles = await dbContext.CommitFiles
                        .Where(file => file.CommitId == commit.Id)
                        .ToListAsync(cancellationToken);
                    dbContext.CommitFiles.RemoveRange(existingCommitFiles);
                    commit.CommitFiles.Clear();
                    result.CommitFilesRemoved += existingCommitFiles.Count;
                }

                if (commitJson.TryGetProperty("stats", out var statsJson) &&
                    statsJson.ValueKind == JsonValueKind.Object)
                {
                    commit.Additions = GetOptionalInt32(statsJson, "additions");
                    commit.Deletions = GetOptionalInt32(statsJson, "deletions");
                }

                commit.FilesChanged = commitJson.TryGetProperty("files", out var filesJson) &&
                                      filesJson.ValueKind == JsonValueKind.Array
                    ? filesJson.GetArrayLength()
                    : null;

                if (commitJson.TryGetProperty("files", out var commitFilesJson) &&
                    commitFilesJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var commitFileJson in commitFilesJson.EnumerateArray())
                    {
                        commit.CommitFiles.Add(new CommitFile
                        {
                            Filename = commitFileJson.GetProperty("filename").GetString()!,
                            PreviousFilename = GetOptionalString(
                                commitFileJson,
                                "previous_filename"),
                            Status = GetOptionalString(commitFileJson, "status"),
                            Additions = GetOptionalInt32(commitFileJson, "additions"),
                            Deletions = GetOptionalInt32(commitFileJson, "deletions"),
                            Changes = GetOptionalInt32(commitFileJson, "changes"),
                            Patch = GetOptionalString(commitFileJson, "patch"),
                            RawJson = commitFileJson.GetRawText()
                        });
                    }
                }

                if (commitInserted)
                {
                    dbContext.Commits.Add(commit);
                    commits.Add(pair.Key, commit);
                    result.CommitsInserted++;
                }
                else
                {
                    result.CommitsUpdated++;
                }

                result.CommitFilesInserted += commit.CommitFiles.Count;
            }

            await dbContext.SaveChangesAsync(cancellationToken);

            var existingLinks = await dbContext.PullRequestCommits
                .Where(link => link.PullRequestId == pullRequest.Id)
                .ToDictionaryAsync(link => link.CommitId, cancellationToken);
            var remoteCommitIds = new HashSet<long>();
            var position = 0;

            foreach (var rawCommit in rawPullRequestCommits)
            {
                using var commitDocument = JsonDocument.Parse(rawCommit);
                var sha = commitDocument.RootElement.GetProperty("sha").GetString()!;
                var commit = commits[sha];
                remoteCommitIds.Add(commit.Id);

                if (existingLinks.TryGetValue(commit.Id, out var link))
                {
                    link.Position = position;
                    result.PullRequestCommitLinksUpdated++;
                }
                else
                {
                    dbContext.PullRequestCommits.Add(new PullRequestCommit
                    {
                        PullRequestId = pullRequest.Id,
                        CommitId = commit.Id,
                        Position = position
                    });
                    result.PullRequestCommitLinksInserted++;
                }

                position++;
            }

            var staleLinks = existingLinks.Values
                .Where(link => !remoteCommitIds.Contains(link.CommitId))
                .ToList();
            dbContext.PullRequestCommits.RemoveRange(staleLinks);
            result.PullRequestCommitLinksRemoved += staleLinks.Count;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            processed++;
            if (processed % 10 == 0 || processed == rawPullRequests.Count)
            {
                Console.WriteLine(
                    $"Pull request elaborate: {processed}/{rawPullRequests.Count}");
            }
        }

        return result.ToImmutable();
    }

    private User? UpsertUser(
        JsonElement parent,
        string propertyName,
        IDictionary<long, User> users,
        DateTime syncedAt,
        MutablePullRequestImportResult result)
    {
        if (!parent.TryGetProperty(propertyName, out var userJson) ||
            userJson.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var githubId = userJson.GetProperty("id").GetInt64();
        var inserted = !users.TryGetValue(githubId, out var user);
        user ??= new User { GithubId = githubId };
        user.NodeId = GetOptionalString(userJson, "node_id");
        user.Login = userJson.GetProperty("login").GetString()!;
        user.AvatarUrl = GetOptionalString(userJson, "avatar_url");
        user.HtmlUrl = GetOptionalString(userJson, "html_url");
        user.UserType = GetOptionalString(userJson, "type");
        user.SiteAdmin = GetOptionalBoolean(userJson, "site_admin");
        user.SyncedAt = syncedAt;
        user.RawJson = userJson.GetRawText();

        if (inserted)
        {
            dbContext.Users.Add(user);
            users.Add(githubId, user);
            result.UsersInserted++;
        }

        return user;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? GetOptionalBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static int? GetOptionalInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : null;

    private static DateTime GetRequiredUtcDateTime(
        JsonElement element,
        string propertyName) =>
        element.GetProperty(propertyName).GetDateTime().ToUniversalTime();

    private static DateTime? GetOptionalUtcDateTime(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !property.TryGetDateTime(out var value))
        {
            return null;
        }

        return value.ToUniversalTime();
    }

    private static bool IsUpdatedSince(string rawPullRequest, DateTime since)
    {
        using var document = JsonDocument.Parse(rawPullRequest);
        return GetRequiredUtcDateTime(document.RootElement, "updated_at") >=
               since.ToUniversalTime();
    }

    internal static void ValidateCompleteness(
        int pullRequestNumber,
        string rawDetail,
        int returnedFiles,
        int returnedCommits)
    {
        using var validationDocument = JsonDocument.Parse(rawDetail);
        var validationRoot = validationDocument.RootElement;
        var expectedFiles = GetOptionalInt32(validationRoot, "changed_files");
        var expectedCommits = GetOptionalInt32(validationRoot, "commits");

        if (expectedFiles.HasValue && returnedFiles != expectedFiles.Value)
        {
            throw new InvalidOperationException(
                $"Pull request #{pullRequestNumber}: GitHub dichiara {expectedFiles.Value} file " +
                $"ma l'API ne ha restituiti {returnedFiles}. " +
                "I dati esistenti vengono conservati per evitare un'importazione parziale.");
        }

        if (expectedCommits.HasValue && returnedCommits != expectedCommits.Value)
        {
            throw new InvalidOperationException(
                $"Pull request #{pullRequestNumber}: GitHub dichiara {expectedCommits.Value} commit " +
                $"ma l'API della pull request ne ha restituiti {returnedCommits}. " +
                "I dati esistenti vengono conservati per evitare un'importazione parziale.");
        }
    }

    private sealed class MutablePullRequestImportResult(int totalPullRequests)
    {
        public int TotalPullRequests { get; } = totalPullRequests;
        public int UsersInserted { get; set; }
        public int PullRequestsInserted { get; set; }
        public int PullRequestsUpdated { get; set; }
        public int PullRequestFilesInserted { get; set; }
        public int PullRequestFilesUpdated { get; set; }
        public int PullRequestFilesRemoved { get; set; }
        public int CommitsInserted { get; set; }
        public int CommitsUpdated { get; set; }
        public int CommitFilesInserted { get; set; }
        public int CommitFilesRemoved { get; set; }
        public int PullRequestCommitLinksInserted { get; set; }
        public int PullRequestCommitLinksUpdated { get; set; }
        public int PullRequestCommitLinksRemoved { get; set; }

        public PullRequestImportResult ToImmutable() => new(
            TotalPullRequests,
            UsersInserted,
            PullRequestsInserted,
            PullRequestsUpdated,
            PullRequestFilesInserted,
            PullRequestFilesUpdated,
            PullRequestFilesRemoved,
            CommitsInserted,
            CommitsUpdated,
            CommitFilesInserted,
            CommitFilesRemoved,
            PullRequestCommitLinksInserted,
            PullRequestCommitLinksUpdated,
            PullRequestCommitLinksRemoved);
    }
}

internal sealed record PullRequestImportResult(
    int TotalPullRequests,
    int UsersInserted,
    int PullRequestsInserted,
    int PullRequestsUpdated,
    int PullRequestFilesInserted,
    int PullRequestFilesUpdated,
    int PullRequestFilesRemoved,
    int CommitsInserted,
    int CommitsUpdated,
    int CommitFilesInserted,
    int CommitFilesRemoved,
    int PullRequestCommitLinksInserted,
    int PullRequestCommitLinksUpdated,
    int PullRequestCommitLinksRemoved);
