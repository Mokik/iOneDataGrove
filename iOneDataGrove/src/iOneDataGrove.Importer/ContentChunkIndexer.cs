using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using iOneDataGrove.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class ContentChunkIndexer(IOneDataGroveDbContext dbContext)
{
    internal const string ChunkerVersion = "content-v1";
    private const int SourceBatchSize = 150;
    private const int ChunkBatchSize = 150;
    private const int DeleteBatchSize = 500;

    internal async Task<ContentChunkIndexResult> IndexAsync(
        long repositoryId,
        bool forceFull,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var existing = await ReadExistingSourcesAsync(
                connection,
                repositoryId,
                cancellationToken);
            var (fileSources, files) = await ReadFileSourcesAsync(
                connection,
                repositoryId,
                cancellationToken);
            var candidates = new List<ChunkSourceCandidate>(fileSources);
            candidates.AddRange(await ReadSymbolSourcesAsync(
                connection,
                repositoryId,
                files,
                cancellationToken));
            candidates.AddRange(await ReadEntitySourcesAsync(
                connection,
                repositoryId,
                cancellationToken));

            var seen = new HashSet<ChunkSourceKey>();
            var changed = new List<PreparedChunkSource>();
            var unchangedSources = 0;
            foreach (var candidate in candidates)
            {
                var key = new ChunkSourceKey(candidate.SourceType, candidate.SourceEntityId);
                if (!seen.Add(key))
                {
                    throw new InvalidOperationException(
                        $"Fonte chunk duplicata: {candidate.SourceType}:{candidate.SourceEntityId}.");
                }

                var sourceHash = ComputeHash(
                    candidate.SourceType,
                    candidate.SourceEntityId,
                    candidate.SourceVersion,
                    candidate.Title,
                    candidate.SourcePath,
                    candidate.StartLine,
                    candidate.EndLine,
                    candidate.HtmlUrl,
                    candidate.Content);
                if (!forceFull &&
                    existing.TryGetValue(key, out var current) &&
                    string.Equals(current.SourceHash, sourceHash, StringComparison.Ordinal) &&
                    string.Equals(current.ChunkerVersion, ChunkerVersion, StringComparison.Ordinal))
                {
                    unchangedSources++;
                    continue;
                }

                var generated = candidate.UseLineChunks
                    ? ContentChunker.SplitLines(
                        candidate.Content,
                        candidate.StartLine ?? 1)
                    : ContentChunker.SplitText(candidate.Content);
                changed.Add(new PreparedChunkSource(
                    candidate,
                    sourceHash,
                    generated.Select((chunk, ordinal) => new PreparedChunk(
                        ordinal,
                        chunk.Content,
                        ComputeHash(chunk.Content),
                        chunk.Content.Length,
                        Math.Max(1, (chunk.Content.Length + 3) / 4),
                        chunk.StartLine,
                        chunk.EndLine)).ToArray()));
            }

            var staleSourceIds = existing
                .Where(item => !seen.Contains(item.Key))
                .Select(item => item.Value.Id)
                .ToArray();

            if (changed.Count > 0 || staleSourceIds.Length > 0)
            {
                await ReplaceChangedSourcesAsync(
                    connection,
                    repositoryId,
                    changed,
                    staleSourceIds,
                    cancellationToken);
            }

            var totalChunks = await CountChunksAsync(
                connection,
                repositoryId,
                cancellationToken);
            return new ContentChunkIndexResult(
                candidates.Count,
                changed.Count,
                unchangedSources,
                staleSourceIds.Length,
                changed.Sum(source => source.Chunks.Count),
                totalChunks);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    internal static string ComputeHash(params object?[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            builder.Append(text.Length).Append(':').Append(text).Append('|');
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private static async Task<Dictionary<ChunkSourceKey, ExistingChunkSource>>
        ReadExistingSourcesAsync(
            DbConnection connection,
            long repositoryId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, source_type, source_entity_id, source_hash, chunker_version
            FROM knowledge.chunk_sources
            WHERE repository_id = @repository_id;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);

        var result = new Dictionary<ChunkSourceKey, ExistingChunkSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new ChunkSourceKey(reader.GetString(1), reader.GetInt64(2));
            result.Add(key, new ExistingChunkSource(
                reader.GetInt64(0),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return result;
    }

    private static async Task<(IReadOnlyList<ChunkSourceCandidate> Sources,
        IReadOnlyDictionary<long, RepositoryFileContent> Files)> ReadFileSourcesAsync(
            DbConnection connection,
            long repositoryId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, blob_sha, path, html_url, content
            FROM github.repository_files
            WHERE repository_id = @repository_id
              AND NOT is_deleted
              AND length(btrim(content)) > 0
            ORDER BY id;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);

        var sources = new List<ChunkSourceCandidate>();
        var files = new Dictionary<long, RepositoryFileContent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var blobSha = reader.GetString(1);
            var path = reader.GetString(2);
            var htmlUrl = reader.GetString(3);
            var content = reader.GetString(4);
            files.Add(id, new RepositoryFileContent(blobSha, path, htmlUrl, content));
            sources.Add(new ChunkSourceCandidate(
                "repository_file",
                id,
                blobSha,
                path,
                path,
                1,
                Math.Max(1, CountLines(content)),
                htmlUrl,
                content,
                true));
        }

        return (sources, files);
    }

    private static async Task<IReadOnlyList<ChunkSourceCandidate>> ReadSymbolSourcesAsync(
        DbConnection connection,
        long repositoryId,
        IReadOnlyDictionary<long, RepositoryFileContent> files,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                symbol.id,
                symbol.repository_file_id,
                symbol.kind,
                symbol.qualified_name,
                symbol.start_line,
                symbol.end_line
            FROM knowledge.code_symbols AS symbol
            WHERE symbol.repository_id = @repository_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM knowledge.code_symbols AS child
                  WHERE child.repository_file_id = symbol.repository_file_id
                    AND child.containing_symbol = symbol.qualified_name
              )
            ORDER BY symbol.id;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);

        var sources = new List<ChunkSourceCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var fileId = reader.GetInt64(1);
            if (!files.TryGetValue(fileId, out var file)) continue;

            var kind = reader.GetString(2);
            var qualifiedName = reader.GetString(3);
            var startLine = reader.GetInt32(4);
            var endLine = reader.GetInt32(5);
            var content = ContentChunker.ExtractLines(file.Content, startLine, endLine);
            if (string.IsNullOrWhiteSpace(content)) continue;

            sources.Add(new ChunkSourceCandidate(
                "code_symbol",
                id,
                $"{file.BlobSha}:{kind}:{qualifiedName}:{startLine}:{endLine}",
                qualifiedName,
                file.Path,
                startLine,
                endLine,
                $"{file.HtmlUrl}#L{startLine}",
                content,
                true));
        }

        return sources;
    }

    private static async Task<IReadOnlyList<ChunkSourceCandidate>> ReadEntitySourcesAsync(
        DbConnection connection,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                'issue'::text,
                issue.id,
                COALESCE(issue.updated_at, issue.synced_at)::text,
                '#' || issue.number || ' · ' || issue.title,
                issue.html_url,
                issue.title || chr(10) || chr(10) || COALESCE(issue.body, '')
            FROM github.issues AS issue
            WHERE issue.repository_id = @repository_id
              AND length(btrim(issue.title || ' ' || COALESCE(issue.body, ''))) > 0

            UNION ALL

            SELECT
                'issue_comment',
                comment.id,
                COALESCE(comment.updated_at, comment.synced_at)::text,
                'Commento su #' || issue.number || ' · ' || issue.title,
                comment.html_url,
                COALESCE(comment.body, '')
            FROM github.issue_comments AS comment
            INNER JOIN github.issues AS issue ON issue.id = comment.issue_id
            WHERE comment.repository_id = @repository_id
              AND length(btrim(COALESCE(comment.body, ''))) > 0

            UNION ALL

            SELECT
                'pull_request',
                pull_request.id,
                COALESCE(pull_request.updated_at, pull_request.synced_at)::text,
                'PR #' || pull_request.number || ' · ' || pull_request.title,
                pull_request.html_url,
                pull_request.title || chr(10) || chr(10) || COALESCE(pull_request.body, '')
            FROM github.pull_requests AS pull_request
            WHERE pull_request.repository_id = @repository_id
              AND length(btrim(pull_request.title || ' ' || COALESCE(pull_request.body, ''))) > 0

            UNION ALL

            SELECT
                'commit',
                commit.id,
                commit.sha,
                LEFT(commit.sha, 8) || ' · ' || SPLIT_PART(commit.message, chr(10), 1),
                commit.html_url,
                commit.message
            FROM github.commits AS commit
            WHERE commit.repository_id = @repository_id
              AND length(btrim(commit.message)) > 0

            ORDER BY 1, 2;
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "repository_id", repositoryId);

        var sources = new List<ChunkSourceCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new ChunkSourceCandidate(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                null,
                null,
                null,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                false));
        }

        return sources;
    }

    private static async Task ReplaceChangedSourcesAsync(
        DbConnection connection,
        long repositoryId,
        IReadOnlyList<PreparedChunkSource> changed,
        IReadOnlyList<long> staleSourceIds,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var sourceIds = new Dictionary<ChunkSourceKey, long>();
            foreach (var batch in changed.Chunk(SourceBatchSize))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                var sql = new StringBuilder("""
                    INSERT INTO knowledge.chunk_sources
                    (
                        repository_id, source_type, source_entity_id, source_version,
                        source_hash, chunker_version, title, source_path,
                        start_line, end_line, html_url, indexed_at
                    )
                    VALUES
                    """);

                AddParameter(command, "repository_id", repositoryId);
                for (var index = 0; index < batch.Length; index++)
                {
                    if (index > 0) sql.Append(',');
                    sql.AppendLine();
                    sql.Append($"(@repository_id, @source_type_{index}, @source_entity_id_{index}, ");
                    sql.Append($"@source_version_{index}, @source_hash_{index}, @chunker_version, ");
                    sql.Append($"@title_{index}, @source_path_{index}, @start_line_{index}, ");
                    sql.Append($"@end_line_{index}, @html_url_{index}, NOW())");

                    var source = batch[index];
                    AddParameter(command, $"source_type_{index}", source.Candidate.SourceType);
                    AddParameter(command, $"source_entity_id_{index}", source.Candidate.SourceEntityId);
                    AddParameter(command, $"source_version_{index}", source.Candidate.SourceVersion);
                    AddParameter(command, $"source_hash_{index}", source.SourceHash);
                    AddParameter(command, $"title_{index}", source.Candidate.Title);
                    AddParameter(command, $"source_path_{index}", source.Candidate.SourcePath);
                    AddParameter(command, $"start_line_{index}", source.Candidate.StartLine);
                    AddParameter(command, $"end_line_{index}", source.Candidate.EndLine);
                    AddParameter(command, $"html_url_{index}", source.Candidate.HtmlUrl);
                }

                AddParameter(command, "chunker_version", ChunkerVersion);
                sql.Append("""
                    
                    ON CONFLICT (repository_id, source_type, source_entity_id) DO UPDATE SET
                        source_version = EXCLUDED.source_version,
                        source_hash = EXCLUDED.source_hash,
                        chunker_version = EXCLUDED.chunker_version,
                        title = EXCLUDED.title,
                        source_path = EXCLUDED.source_path,
                        start_line = EXCLUDED.start_line,
                        end_line = EXCLUDED.end_line,
                        html_url = EXCLUDED.html_url,
                        indexed_at = NOW()
                    RETURNING id, source_type, source_entity_id;
                    """);
                command.CommandText = sql.ToString();

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    sourceIds.Add(
                        new ChunkSourceKey(reader.GetString(1), reader.GetInt64(2)),
                        reader.GetInt64(0));
                }
            }

            foreach (var batch in sourceIds.Values.Chunk(DeleteBatchSize))
            {
                await DeleteByIdsAsync(
                    connection,
                    transaction,
                    "knowledge.content_chunks",
                    "chunk_source_id",
                    batch,
                    cancellationToken);
            }

            var chunks = changed
                .SelectMany(source =>
                {
                    var key = new ChunkSourceKey(
                        source.Candidate.SourceType,
                        source.Candidate.SourceEntityId);
                    var sourceId = sourceIds[key];
                    return source.Chunks.Select(chunk => new ChunkInsert(sourceId, chunk));
                })
                .ToArray();

            foreach (var batch in chunks.Chunk(ChunkBatchSize))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                var sql = new StringBuilder("""
                    INSERT INTO knowledge.content_chunks
                    (
                        chunk_source_id, repository_id, ordinal, content, content_hash,
                        character_count, estimated_tokens, start_line, end_line,
                        metadata, indexed_at
                    )
                    VALUES
                    """);
                AddParameter(command, "repository_id", repositoryId);
                for (var index = 0; index < batch.Length; index++)
                {
                    if (index > 0) sql.Append(',');
                    sql.AppendLine();
                    sql.Append($"(@chunk_source_id_{index}, @repository_id, @ordinal_{index}, ");
                    sql.Append($"@content_{index}, @content_hash_{index}, @character_count_{index}, ");
                    sql.Append($"@estimated_tokens_{index}, @start_line_{index}, @end_line_{index}, ");
                    sql.Append("'{}'::jsonb, NOW())");

                    var item = batch[index];
                    AddParameter(command, $"chunk_source_id_{index}", item.SourceId);
                    AddParameter(command, $"ordinal_{index}", item.Chunk.Ordinal);
                    AddParameter(command, $"content_{index}", item.Chunk.Content);
                    AddParameter(command, $"content_hash_{index}", item.Chunk.ContentHash);
                    AddParameter(command, $"character_count_{index}", item.Chunk.CharacterCount);
                    AddParameter(command, $"estimated_tokens_{index}", item.Chunk.EstimatedTokens);
                    AddParameter(command, $"start_line_{index}", item.Chunk.StartLine);
                    AddParameter(command, $"end_line_{index}", item.Chunk.EndLine);
                }

                sql.Append(';');
                command.CommandText = sql.ToString();
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var batch in staleSourceIds.Chunk(DeleteBatchSize))
            {
                await DeleteByIdsAsync(
                    connection,
                    transaction,
                    "knowledge.chunk_sources",
                    "id",
                    batch,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task DeleteByIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string column,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameters = new string[ids.Count];
        for (var index = 0; index < ids.Count; index++)
        {
            parameters[index] = $"@id_{index}";
            AddParameter(command, parameters[index], ids[index]);
        }

        command.CommandText =
            $"DELETE FROM {table} WHERE {column} IN ({string.Join(", ", parameters)});";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountChunksAsync(
        DbConnection connection,
        long repositoryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)::integer
            FROM knowledge.content_chunks
            WHERE repository_id = @repository_id;
            """;
        AddParameter(command, "repository_id", repositoryId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static int CountLines(string content)
    {
        if (content.Length == 0) return 0;
        var count = 1;
        foreach (var character in content)
        {
            if (character == '\n') count++;
        }

        return count;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

internal readonly record struct ChunkSourceKey(string SourceType, long SourceEntityId);

internal sealed record ExistingChunkSource(long Id, string SourceHash, string ChunkerVersion);

internal sealed record RepositoryFileContent(
    string BlobSha,
    string Path,
    string HtmlUrl,
    string Content);

internal sealed record ChunkSourceCandidate(
    string SourceType,
    long SourceEntityId,
    string SourceVersion,
    string Title,
    string? SourcePath,
    int? StartLine,
    int? EndLine,
    string? HtmlUrl,
    string Content,
    bool UseLineChunks);

internal sealed record PreparedChunk(
    int Ordinal,
    string Content,
    string ContentHash,
    int CharacterCount,
    int EstimatedTokens,
    int? StartLine,
    int? EndLine);

internal sealed record PreparedChunkSource(
    ChunkSourceCandidate Candidate,
    string SourceHash,
    IReadOnlyList<PreparedChunk> Chunks);

internal sealed record ChunkInsert(long SourceId, PreparedChunk Chunk);

internal sealed record ContentChunkIndexResult(
    int Sources,
    int IndexedSources,
    int UnchangedSources,
    int RemovedSources,
    int WrittenChunks,
    int TotalChunks);
