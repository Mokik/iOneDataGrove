using System.Text.Json;
using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class GitHubIssueImporter(
    IOneDataGroveDbContext dbContext,
    string token)
{
    public async Task<IssueImportResult> ImportAsync(
        long repositoryId,
        string owner,
        string repositoryName,
        DateTime? since = null,
        CancellationToken cancellationToken = default)
    {
        using var github = new GitHubApiClient(token);
        var escapedOwner = Uri.EscapeDataString(owner);
        var escapedRepository = Uri.EscapeDataString(repositoryName);
        var commentSinceQuery = since.HasValue
            ? $"&since={Uri.EscapeDataString(since.Value.ToUniversalTime().ToString("O"))}"
            : string.Empty;

        // GitHub non aggiorna sempre issue.updated_at quando viene aggiunto o rimosso
        // un commento. Il catalogo completo serve quindi per avere conteggi autorevoli;
        // i contenuti delle issue vengono comunque aggiornati solo se sono cambiati.
        var rawIssues = await github.GetAllPagesAsync(
            page => $"repos/{escapedOwner}/{escapedRepository}/issues" +
                    $"?state=all&sort=updated&direction=asc&per_page=100&page={page}",
            cancellationToken);

        var incrementalRawComments = await github.GetAllPagesAsync(
            page => $"repos/{escapedOwner}/{escapedRepository}/issues/comments" +
                    $"?sort=updated&direction=asc&per_page=100&page={page}{commentSinceQuery}",
            cancellationToken);

        var repository = await dbContext.Repositories.SingleAsync(
            item => item.Id == repositoryId,
            cancellationToken);
        var users = await dbContext.Users.ToDictionaryAsync(
            user => user.GithubId,
            cancellationToken);
        var issues = await dbContext.Issues
            .Where(issue => issue.RepositoryId == repositoryId)
            .ToDictionaryAsync(issue => issue.GithubId, cancellationToken);
        var comments = await dbContext.IssueComments
            .Where(comment => comment.RepositoryId == repositoryId)
            .ToDictionaryAsync(comment => comment.GithubId, cancellationToken);
        var importedCommentCounts = await dbContext.IssueComments
            .AsNoTracking()
            .Where(comment => comment.RepositoryId == repositoryId)
            .GroupBy(comment => comment.Issue.Number)
            .Select(group => new { IssueNumber = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.IssueNumber, item => item.Count, cancellationToken);

        var expectedCommentCounts = issues.Values.ToDictionary(
            issue => issue.Number,
            issue => issue.CommentsCount);
        foreach (var rawIssue in rawIssues)
        {
            using var document = JsonDocument.Parse(rawIssue);
            var root = document.RootElement;
            if (!root.TryGetProperty("pull_request", out _))
            {
                expectedCommentCounts[root.GetProperty("number").GetInt32()] =
                    root.GetProperty("comments").GetInt32();
            }
        }

        var rawCommentsById = new Dictionary<long, string>();
        AddRawComments(rawCommentsById, incrementalRawComments);

        var incomingNewCommentCounts = GetIncomingNewCommentCounts(
            incrementalRawComments,
            comments,
            expectedCommentCounts);
        var predictedCommentCounts = expectedCommentCounts.Keys.ToDictionary(
            issueNumber => issueNumber,
            issueNumber => importedCommentCounts.GetValueOrDefault(issueNumber) +
                incomingNewCommentCounts.GetValueOrDefault(issueNumber));
        var commentReconciliationIssueNumbers = GetCommentReconciliationIssueNumbers(
            expectedCommentCounts,
            predictedCommentCounts);
        var authoritativeCommentIds = new Dictionary<int, HashSet<long>>();

        foreach (var issueNumber in commentReconciliationIssueNumbers)
        {
            var completeIssueComments = await github.GetAllPagesAsync(
                page => $"repos/{escapedOwner}/{escapedRepository}/issues/{issueNumber}/comments" +
                        $"?sort=created&direction=asc&per_page=100&page={page}",
                cancellationToken);
            authoritativeCommentIds[issueNumber] = completeIssueComments
                .Select(GetCommentGithubId)
                .ToHashSet();
            AddRawComments(rawCommentsById, completeIssueComments);
        }

        var syncedAt = DateTime.UtcNow;
        var usersInserted = 0;
        var issuesInserted = 0;
        var issuesUpdated = 0;
        var pullRequestsSkipped = 0;

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var rawIssue in rawIssues)
        {
            using var document = JsonDocument.Parse(rawIssue);
            var root = document.RootElement;

            if (root.TryGetProperty("pull_request", out _))
            {
                pullRequestsSkipped++;
                continue;
            }

            var githubId = root.GetProperty("id").GetInt64();
            var inserted = !issues.TryGetValue(githubId, out var issue);
            issue ??= new Issue { GithubId = githubId };
            var rawUpdatedAt = GetRequiredUtcDateTime(root, "updated_at");
            var currentCommentsCount = root.GetProperty("comments").GetInt32();

            if (!inserted && since.HasValue &&
                rawUpdatedAt < since.Value.ToUniversalTime())
            {
                if (issue.CommentsCount != currentCommentsCount)
                {
                    issue.CommentsCount = currentCommentsCount;
                    issue.SyncedAt = syncedAt;
                    issue.RawJson = rawIssue;
                    issuesUpdated++;
                }

                continue;
            }

            issue.Repository = repository;
            issue.NodeId = GetOptionalString(root, "node_id");
            issue.Number = root.GetProperty("number").GetInt32();
            issue.AuthorUser = UpsertUser(root, "user", users, syncedAt, ref usersInserted);
            issue.Title = root.GetProperty("title").GetString()!;
            issue.Body = GetOptionalString(root, "body");
            issue.State = root.GetProperty("state").GetString()!;
            issue.StateReason = GetOptionalString(root, "state_reason");
            issue.Locked = root.GetProperty("locked").GetBoolean();
            issue.CommentsCount = currentCommentsCount;
            issue.HtmlUrl = GetOptionalString(root, "html_url");
            issue.CreatedAt = GetRequiredUtcDateTime(root, "created_at");
            issue.UpdatedAt = rawUpdatedAt;
            issue.ClosedAt = GetOptionalUtcDateTime(root, "closed_at");
            issue.ClosedByUser = UpsertUser(
                root,
                "closed_by",
                users,
                syncedAt,
                ref usersInserted);
            issue.SyncedAt = syncedAt;
            issue.RawJson = rawIssue;

            if (inserted)
            {
                dbContext.Issues.Add(issue);
                issues.Add(githubId, issue);
                issuesInserted++;
            }
            else
            {
                issuesUpdated++;
            }
        }

        var issuesByNumber = issues.Values.ToDictionary(issue => issue.Number);
        var commentsInserted = 0;
        var commentsUpdated = 0;
        var commentsRemoved = 0;
        var pullRequestCommentsSkipped = 0;

        foreach (var rawComment in rawCommentsById.Values)
        {
            using var document = JsonDocument.Parse(rawComment);
            var root = document.RootElement;
            var issueNumber = GetIssueNumber(root.GetProperty("issue_url").GetString()!);

            if (!issuesByNumber.TryGetValue(issueNumber, out var issue))
            {
                pullRequestCommentsSkipped++;
                continue;
            }

            var githubId = root.GetProperty("id").GetInt64();
            var inserted = !comments.TryGetValue(githubId, out var comment);
            comment ??= new IssueComment { GithubId = githubId };

            comment.Repository = repository;
            comment.Issue = issue;
            comment.NodeId = GetOptionalString(root, "node_id");
            comment.AuthorUser = UpsertUser(root, "user", users, syncedAt, ref usersInserted);
            comment.Body = GetOptionalString(root, "body");
            comment.AuthorAssociation = GetOptionalString(root, "author_association");
            comment.HtmlUrl = GetOptionalString(root, "html_url");
            comment.CreatedAt = GetRequiredUtcDateTime(root, "created_at");
            comment.UpdatedAt = GetRequiredUtcDateTime(root, "updated_at");
            comment.SyncedAt = syncedAt;
            comment.RawJson = rawComment;

            if (inserted)
            {
                dbContext.IssueComments.Add(comment);
                comments.Add(githubId, comment);
                commentsInserted++;
            }
            else
            {
                commentsUpdated++;
            }
        }

        foreach (var (issueNumber, authoritativeIds) in authoritativeCommentIds)
        {
            if (!issuesByNumber.TryGetValue(issueNumber, out var issue))
            {
                continue;
            }

            var staleCommentIds = GetStaleCommentIds(
                comments.Values
                    .Where(comment => ReferenceEquals(comment.Issue, issue) ||
                        (issue.Id != 0 && comment.IssueId == issue.Id))
                    .Select(comment => comment.GithubId),
                authoritativeIds);
            foreach (var githubId in staleCommentIds)
            {
                if (comments.Remove(githubId, out var staleComment))
                {
                    dbContext.IssueComments.Remove(staleComment);
                    commentsRemoved++;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IssueImportResult(
            usersInserted,
            issuesInserted,
            issuesUpdated,
            commentsInserted,
            commentsUpdated,
            commentsRemoved,
            commentReconciliationIssueNumbers.Count,
            pullRequestsSkipped,
            pullRequestCommentsSkipped,
            syncedAt);
    }

    internal static IReadOnlyList<int> GetCommentReconciliationIssueNumbers(
        IReadOnlyDictionary<int, int> expectedCommentCounts,
        IReadOnlyDictionary<int, int> predictedCommentCounts) =>
        expectedCommentCounts
            .Where(item => item.Value != predictedCommentCounts.GetValueOrDefault(item.Key))
            .Select(item => item.Key)
            .OrderBy(item => item)
            .ToList();

    internal static IReadOnlyList<long> GetStaleCommentIds(
        IEnumerable<long> importedCommentIds,
        IReadOnlySet<long> authoritativeCommentIds) =>
        importedCommentIds
            .Where(githubId => !authoritativeCommentIds.Contains(githubId))
            .OrderBy(githubId => githubId)
            .ToList();

    private static Dictionary<int, int> GetIncomingNewCommentCounts(
        IEnumerable<string> rawComments,
        IReadOnlyDictionary<long, IssueComment> importedComments,
        IReadOnlyDictionary<int, int> expectedCommentCounts)
    {
        var counts = new Dictionary<int, int>();
        var seenNewCommentIds = new HashSet<long>();
        foreach (var rawComment in rawComments)
        {
            using var document = JsonDocument.Parse(rawComment);
            var root = document.RootElement;
            var githubId = root.GetProperty("id").GetInt64();
            if (importedComments.ContainsKey(githubId) || !seenNewCommentIds.Add(githubId))
            {
                continue;
            }

            var issueNumber = GetIssueNumber(root.GetProperty("issue_url").GetString()!);
            if (expectedCommentCounts.ContainsKey(issueNumber))
            {
                counts[issueNumber] = counts.GetValueOrDefault(issueNumber) + 1;
            }
        }

        return counts;
    }

    private static void AddRawComments(
        IDictionary<long, string> destination,
        IEnumerable<string> rawComments)
    {
        foreach (var rawComment in rawComments)
        {
            using var document = JsonDocument.Parse(rawComment);
            destination[document.RootElement.GetProperty("id").GetInt64()] = rawComment;
        }
    }

    private static long GetCommentGithubId(string rawComment)
    {
        using var document = JsonDocument.Parse(rawComment);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private User? UpsertUser(
        JsonElement parent,
        string propertyName,
        IDictionary<long, User> users,
        DateTime syncedAt,
        ref int usersInserted)
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
            usersInserted++;
        }

        return user;
    }

    private static int GetIssueNumber(string issueUrl)
    {
        var separator = issueUrl.LastIndexOf('/');

        if (separator < 0 || !int.TryParse(issueUrl[(separator + 1)..], out var number))
        {
            throw new FormatException($"issue_url GitHub non valido: {issueUrl}");
        }

        return number;
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
}

internal sealed record IssueImportResult(
    int UsersInserted,
    int IssuesInserted,
    int IssuesUpdated,
    int CommentsInserted,
    int CommentsUpdated,
    int CommentsRemoved,
    int CommentReconciliationIssues,
    int PullRequestsSkipped,
    int PullRequestCommentsSkipped,
    DateTime SyncedAt);
