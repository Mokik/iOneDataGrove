using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using iOneDataGrove.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed partial class KnowledgeLinkIndexer(IOneDataGroveDbContext dbContext)
{
    // Add missing edges for one immutable commit, without rebuilding the repository index.
    public Task<int> IndexCommitAsync(long repositoryId, string fullName, long commitId, CancellationToken ct) =>
        IndexCommitsAsync(repositoryId, fullName, [commitId], ct);

    public async Task<int> IndexCommitsAsync(long repositoryId, string fullName, long[] commitIds, CancellationToken ct)
    {
        if (commitIds.Length == 0) return 0;
        var commits = await dbContext.Commits.AsNoTracking()
            .Where(c => c.RepositoryId == repositoryId && commitIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Message }).ToListAsync(ct);
        var targets = await ReadReferenceTargetsAsync(repositoryId, ct);
        var links = BuildTextualLinks(commits.Select(c => new LinkTextSource("commit", c.Id, "message", c.Message)), targets, fullName);
        await dbContext.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var count = await InsertTextualLinksAsync(connection, transaction, repositoryId, links, ct);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO knowledge.entity_links
                    (repository_id, source_type, source_id, target_type, target_id,
                     relation_type, evidence_type, evidence_key, evidence_text, refreshed_at)
                SELECT @repository_id, 'commit', c.id, 'repository_file', f.id,
                    'modifies_file', 'github_api', 'commit_files:' || cf.filename, cf.filename, NOW()
                FROM github.commits c
                JOIN github.commit_files cf ON cf.commit_id = c.id
                JOIN github.repository_files f ON f.repository_id = c.repository_id AND f.path = cf.filename
                WHERE c.repository_id = @repository_id AND c.id = ANY(@commit_ids)
                ON CONFLICT DO NOTHING;
                """;
            AddParameter(command, "repository_id", repositoryId);
            AddParameter(command, "commit_ids", commitIds);
            count += await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return count;
        }
        finally { await dbContext.Database.CloseConnectionAsync(); }
    }

    public async Task<KnowledgeLinkIndexResult> IndexAsync(
        long repositoryId,
        string repositoryFullName,
        CancellationToken cancellationToken = default)
    {
        var targets = await ReadReferenceTargetsAsync(repositoryId, cancellationToken);
        var documents = await ReadTextSourcesAsync(repositoryId, cancellationToken);
        var textualLinks = BuildTextualLinks(documents, targets, repositoryFullName);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM knowledge.entity_links WHERE repository_id = @repository_id;",
                    repositoryId,
                    cancellationToken);

                var pullRequestCommits = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO knowledge.entity_links
                    (
                        repository_id, source_type, source_id, target_type, target_id,
                        relation_type, evidence_type, evidence_key, evidence_text, refreshed_at
                    )
                    SELECT
                        @repository_id, 'pull_request', link.pull_request_id, 'commit', link.commit_id,
                        'contains_commit', 'github_api', 'pull_request_commits', NULL, NOW()
                    FROM github.pull_request_commits AS link
                    INNER JOIN github.pull_requests AS pull_request
                        ON pull_request.id = link.pull_request_id
                    WHERE pull_request.repository_id = @repository_id
                    ON CONFLICT DO NOTHING;
                    """,
                    repositoryId,
                    cancellationToken);

                var pullRequestFiles = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO knowledge.entity_links
                    (
                        repository_id, source_type, source_id, target_type, target_id,
                        relation_type, evidence_type, evidence_key, evidence_text, refreshed_at
                    )
                    SELECT
                        @repository_id, 'pull_request', pull_file.pull_request_id,
                        'repository_file', source.id, 'modifies_file', 'github_api',
                        'pull_request_files:' || pull_file.filename, pull_file.filename, NOW()
                    FROM github.pull_request_files AS pull_file
                    INNER JOIN github.pull_requests AS pull_request
                        ON pull_request.id = pull_file.pull_request_id
                    INNER JOIN github.repository_files AS source
                        ON source.repository_id = pull_request.repository_id
                       AND source.path = pull_file.filename
                    WHERE pull_request.repository_id = @repository_id
                    ON CONFLICT DO NOTHING;
                    """,
                    repositoryId,
                    cancellationToken);

                var commitFiles = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO knowledge.entity_links
                    (
                        repository_id, source_type, source_id, target_type, target_id,
                        relation_type, evidence_type, evidence_key, evidence_text, refreshed_at
                    )
                    SELECT
                        @repository_id, 'commit', commit_file.commit_id,
                        'repository_file', source.id, 'modifies_file', 'github_api',
                        'commit_files:' || commit_file.filename, commit_file.filename, NOW()
                    FROM github.commit_files AS commit_file
                    INNER JOIN github.commits AS commit
                        ON commit.id = commit_file.commit_id
                    INNER JOIN github.repository_files AS source
                        ON source.repository_id = commit.repository_id
                       AND source.path = commit_file.filename
                    WHERE commit.repository_id = @repository_id
                    ON CONFLICT DO NOTHING;
                    """,
                    repositoryId,
                    cancellationToken);

                var declaredSymbols = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO knowledge.entity_links
                    (
                        repository_id, source_type, source_id, target_type, target_id,
                        relation_type, evidence_type, evidence_key, evidence_text, refreshed_at
                    )
                    SELECT
                        @repository_id, 'repository_file', symbol.repository_file_id,
                        'code_symbol', symbol.id, 'declares_symbol', 'structural_index',
                        'code_symbols:' || symbol.id::text, symbol.qualified_name, NOW()
                    FROM knowledge.code_symbols AS symbol
                    WHERE symbol.repository_id = @repository_id
                    ON CONFLICT DO NOTHING;
                    """,
                    repositoryId,
                    cancellationToken);

                var textReferences = await InsertTextualLinksAsync(
                    connection,
                    transaction,
                    repositoryId,
                    textualLinks,
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return new KnowledgeLinkIndexResult(
                    pullRequestCommits,
                    pullRequestFiles,
                    commitFiles,
                    declaredSymbols,
                    textReferences);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    internal static IReadOnlyList<TextReference> ExtractReferences(
        string text,
        string repositoryFullName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var references = new Dictionary<int, TextReference>();
        foreach (Match match in IssueReferenceRegex().Matches(text))
        {
            var qualifier = match.Groups["repository"].Value;
            if (qualifier.Length > 0 &&
                !string.Equals(qualifier, repositoryFullName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(match.Groups["number"].Value, out var number))
            {
                continue;
            }

            var relation = match.Groups["closing"].Success ? "closes" : "references";
            var excerptStart = Math.Max(0, match.Index - 45);
            var excerptLength = Math.Min(text.Length - excerptStart, match.Length + 90);
            var excerpt = WhitespaceRegex().Replace(
                text.Substring(excerptStart, excerptLength),
                " ").Trim();
            var candidate = new TextReference(number, relation, excerpt);

            if (!references.TryGetValue(number, out var existing) ||
                (existing.RelationType == "references" && relation == "closes"))
            {
                references[number] = candidate;
            }
        }

        return references.Values.OrderBy(item => item.Number).ToArray();
    }

    private async Task<Dictionary<int, LinkTarget>> ReadReferenceTargetsAsync(
        long repositoryId,
        CancellationToken cancellationToken)
    {
        var targets = await dbContext.Issues.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId)
            .Select(item => new LinkTarget(item.Number, "issue", item.Id))
            .ToDictionaryAsync(item => item.Number, cancellationToken);

        var pullRequests = await dbContext.PullRequests.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId)
            .Select(item => new LinkTarget(item.Number, "pull_request", item.Id))
            .ToListAsync(cancellationToken);
        foreach (var pullRequest in pullRequests)
        {
            targets[pullRequest.Number] = pullRequest;
        }

        return targets;
    }

    private async Task<IReadOnlyList<LinkTextSource>> ReadTextSourcesAsync(
        long repositoryId,
        CancellationToken cancellationToken)
    {
        var sources = new List<LinkTextSource>();

        var issues = await dbContext.Issues.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId)
            .Select(item => new { item.Id, item.Title, item.Body })
            .ToListAsync(cancellationToken);
        foreach (var issue in issues)
        {
            sources.Add(new LinkTextSource("issue", issue.Id, "title", issue.Title));
            if (!string.IsNullOrWhiteSpace(issue.Body))
            {
                sources.Add(new LinkTextSource("issue", issue.Id, "body", issue.Body));
            }
        }

        var comments = await dbContext.IssueComments.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId && item.Body != null)
            .Select(item => new { item.Id, item.Body })
            .ToListAsync(cancellationToken);
        sources.AddRange(comments.Select(item =>
            new LinkTextSource("issue_comment", item.Id, "body", item.Body!)));

        var pullRequests = await dbContext.PullRequests.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId)
            .Select(item => new { item.Id, item.Title, item.Body })
            .ToListAsync(cancellationToken);
        foreach (var pullRequest in pullRequests)
        {
            sources.Add(new LinkTextSource(
                "pull_request", pullRequest.Id, "title", pullRequest.Title));
            if (!string.IsNullOrWhiteSpace(pullRequest.Body))
            {
                sources.Add(new LinkTextSource(
                    "pull_request", pullRequest.Id, "body", pullRequest.Body));
            }
        }

        var commits = await dbContext.Commits.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId)
            .Select(item => new { item.Id, item.Message })
            .ToListAsync(cancellationToken);
        sources.AddRange(commits.Select(item =>
            new LinkTextSource("commit", item.Id, "message", item.Message)));

        return sources;
    }

    private static IReadOnlyList<KnowledgeLinkCandidate> BuildTextualLinks(
        IEnumerable<LinkTextSource> documents,
        IReadOnlyDictionary<int, LinkTarget> targets,
        string repositoryFullName)
    {
        var links = new List<KnowledgeLinkCandidate>();
        foreach (var document in documents)
        {
            foreach (var reference in ExtractReferences(document.Text, repositoryFullName))
            {
                if (!targets.TryGetValue(reference.Number, out var target) ||
                    (document.SourceType == target.Type && document.SourceId == target.Id))
                {
                    continue;
                }

                links.Add(new KnowledgeLinkCandidate(
                    document.SourceType,
                    document.SourceId,
                    target.Type,
                    target.Id,
                    reference.RelationType,
                    $"{document.Field}:#{reference.Number}",
                    reference.EvidenceText));
            }
        }

        return links;
    }

    private static async Task<int> InsertTextualLinksAsync(
        DbConnection connection,
        DbTransaction transaction,
        long repositoryId,
        IReadOnlyList<KnowledgeLinkCandidate> links,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        foreach (var batch in links.Chunk(150))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var sql = new StringBuilder(
                """
                INSERT INTO knowledge.entity_links
                (
                    repository_id, source_type, source_id, target_type, target_id,
                    relation_type, evidence_type, evidence_key, evidence_text, refreshed_at
                )
                VALUES
                """);
            AddParameter(command, "repository_id", repositoryId);

            for (var index = 0; index < batch.Length; index++)
            {
                if (index > 0)
                {
                    sql.Append(',');
                }

                sql.AppendLine();
                sql.Append(
                    $"(@repository_id, @source_type_{index}, @source_id_{index}, " +
                    $"@target_type_{index}, @target_id_{index}, @relation_type_{index}, " +
                    $"'text_reference', @evidence_key_{index}, @evidence_text_{index}, NOW())");
                AddParameter(command, $"source_type_{index}", batch[index].SourceType);
                AddParameter(command, $"source_id_{index}", batch[index].SourceId);
                AddParameter(command, $"target_type_{index}", batch[index].TargetType);
                AddParameter(command, $"target_id_{index}", batch[index].TargetId);
                AddParameter(command, $"relation_type_{index}", batch[index].RelationType);
                AddParameter(command, $"evidence_key_{index}", batch[index].EvidenceKey);
                AddParameter(command, $"evidence_text_{index}", batch[index].EvidenceText);
            }

            sql.AppendLine();
            sql.Append("ON CONFLICT DO NOTHING;");
            command.CommandText = sql.ToString();
            inserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return inserted;
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    [GeneratedRegex(
        @"(?ix)(?:(?<closing>close[sd]?|fix(?:e[sd])?|resolve[sd]?|chiude|chiudono|risolve|risolvono|corregge|correggono|(?:chius[oaie]|risolt[oaie]|corrett[oaie])\s+da)\s+)?(?:(?<repository>[a-z0-9_.-]+/[a-z0-9_.-]+))?\#(?<number>[1-9][0-9]*)(?![0-9])")]
    private static partial Regex IssueReferenceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal sealed record LinkTarget(int Number, string Type, long Id);

internal sealed record LinkTextSource(string SourceType, long SourceId, string Field, string Text);

internal sealed record TextReference(int Number, string RelationType, string EvidenceText);

internal sealed record KnowledgeLinkCandidate(
    string SourceType,
    long SourceId,
    string TargetType,
    long TargetId,
    string RelationType,
    string EvidenceKey,
    string EvidenceText);

internal sealed record KnowledgeLinkIndexResult(
    int PullRequestCommits,
    int PullRequestFiles,
    int CommitFiles,
    int DeclaredSymbols,
    int TextReferences)
{
    public int Total =>
        PullRequestCommits + PullRequestFiles + CommitFiles + DeclaredSymbols + TextReferences;
}
