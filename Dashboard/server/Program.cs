using iOneDataGrove.Persistence.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(
    builder.Configuration["Dashboard:Url"] ?? "http://127.0.0.1:5088");
builder.Configuration.AddUserSecrets<Program>(optional: true);

var connectionString = builder.Configuration.GetConnectionString("iOneDataGrove");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:iOneDataGrove non è configurata nei Secret utente.");
}

builder.Services.AddDbContext<IOneDataGroveDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
builder.Services.AddHttpClient();
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();
app.UseResponseCompression();
app.UseCors();

app.MapGet("/api/health", async (IOneDataGroveDbContext db, CancellationToken ct) =>
{
    var reachable = await db.Database.CanConnectAsync(ct);
    return reachable
        ? Results.Ok(new { status = "healthy", database = "PostgreSQL", managementEnabled = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/dashboard", async (IOneDataGroveDbContext db, CancellationToken ct) =>
{
    try
    {
        var repositoryCount = await db.Repositories.AsNoTracking().CountAsync(ct);
        var activeRepositoryCount = await db.Repositories.AsNoTracking()
            .CountAsync(item => item.IsSyncEnabled && !item.IsExcluded, ct);
        var pausedRepositoryCount = await db.Repositories.AsNoTracking()
            .CountAsync(item => !item.IsSyncEnabled && !item.IsExcluded, ct);
        var excludedRepositoryCount = await db.Repositories.AsNoTracking()
            .CountAsync(item => item.IsExcluded, ct);
        var userCount = await db.Users.AsNoTracking().CountAsync(ct);
        var issueCount = await db.Issues.AsNoTracking().CountAsync(ct);
        var openIssueCount = await db.Issues.AsNoTracking().CountAsync(item => item.State == "open", ct);
        var commentCount = await db.IssueComments.AsNoTracking().CountAsync(ct);
        var pullRequestCount = await db.PullRequests.AsNoTracking().CountAsync(ct);
        var openPullRequestCount = await db.PullRequests.AsNoTracking().CountAsync(item => item.State == "open", ct);
        var mergedPullRequestCount = await db.PullRequests.AsNoTracking().CountAsync(item => item.Merged, ct);
        var commitCount = await db.Commits.AsNoTracking().CountAsync(ct);
        var pullRequestFileCount = await db.PullRequestFiles.AsNoTracking().CountAsync(ct);
        var commitFileCount = await db.CommitFiles.AsNoTracking().CountAsync(ct);
        var pullRequestCommitCount = await db.PullRequestCommits.AsNoTracking().CountAsync(ct);
        var repositoryFileRecordCount = await db.RepositoryFiles.AsNoTracking().CountAsync(ct);
        var activeSourceFileCount = await db.RepositoryFiles.AsNoTracking()
            .CountAsync(item => !item.IsDeleted, ct);
        var contentChunkCount = await CountContentChunksAsync(db, ct);

        var latestRepositorySync = await db.Repositories.AsNoTracking()
            .MaxAsync(item => (DateTime?)item.SyncedAt, ct);
        var latestIssueSync = await db.Issues.AsNoTracking()
            .MaxAsync(item => (DateTime?)item.SyncedAt, ct);
        var latestPullRequestSync = await db.PullRequests.AsNoTracking()
            .MaxAsync(item => (DateTime?)item.SyncedAt, ct);
        var latestCommitSync = await db.Commits.AsNoTracking()
            .MaxAsync(item => (DateTime?)item.SyncedAt, ct);
        var latestSourceFileSync = await db.RepositoryFiles.AsNoTracking()
            .MaxAsync(item => (DateTime?)item.SyncedAt, ct);

        var latestDataSync = new[]
        {
            latestRepositorySync, latestIssueSync, latestPullRequestSync, latestCommitSync,
            latestSourceFileSync
        }.Where(value => value.HasValue).Max();

        var syncStates = await db.SyncStates.AsNoTracking()
            .OrderBy(item => item.Status == "failed" ? 0 : item.Status == "running" ? 1 : 2)
            .ThenBy(item => item.Repository.FullName)
            .ThenBy(item => item.ResourceType)
            .Select(item => new SyncStateDto(
                item.RepositoryId,
                item.Repository.FullName,
                item.ResourceType,
                item.Status,
                item.LastSuccessfulSync,
                item.LastGithubUpdatedAt,
                item.UpdatedAt))
            .ToListAsync(ct);

        var recentRuns = await db.SyncRuns.AsNoTracking()
            .OrderByDescending(item => item.StartedAt)
            .Take(100)
            .Select(item => new SyncRunDto(
                item.Id,
                item.RepositoryId,
                item.Repository != null ? item.Repository.FullName : null,
                item.ResourceType,
                item.SyncType,
                item.Status,
                item.StartedAt,
                item.CompletedAt,
                item.ItemsRead,
                item.ItemsInserted,
                item.ItemsUpdated,
                item.ItemsFailed,
                item.ErrorMessage))
            .ToListAsync(ct);

        var totalRunCount = await db.SyncRuns.AsNoTracking().CountAsync(ct);
        var failedRunCount = await db.SyncRuns.AsNoTracking()
            .CountAsync(item => item.Status == "failed", ct);

        var repositories = await db.Repositories.AsNoTracking()
            .OrderBy(item => item.FullName)
            .Select(item => new RepositoryDto(
                item.Id,
                item.FullName,
                item.Description,
                item.HtmlUrl,
                item.IsPrivate,
                item.IsArchived,
                item.IsSyncEnabled,
                item.SyncDisabledAt,
                item.IsExcluded,
                item.ExcludedAt,
                item.DefaultBranch,
                item.PrimaryLanguage,
                item.PushedAt,
                item.SyncedAt,
                item.Issues.Count,
                item.Issues.Count(issue => issue.State == "open"),
                item.PullRequests.Count,
                item.PullRequests.Count(pullRequest => pullRequest.State == "open"),
                item.PullRequests.Count(pullRequest => pullRequest.Merged),
                item.Commits.Count))
            .ToListAsync(ct);

        var reviewRepositories = new List<KnowledgeReviewRepositoryDto>();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var reviewCommand = db.Database.GetDbConnection().CreateCommand();
            reviewCommand.CommandText = """
                SELECT
                    repository.id,
                    repository.full_name,
                    COUNT(*)::integer
                FROM knowledge.entity_links AS link
                INNER JOIN github.repositories AS repository
                    ON repository.id = link.repository_id
                LEFT JOIN github.commits AS source_commit
                    ON link.source_type = 'commit' AND source_commit.id = link.source_id
                WHERE link.relation_type = 'references'
                  AND link.evidence_type = 'text_reference'
                  AND NOT repository.is_excluded
                  AND NOT
                  (
                      link.source_type = 'commit'
                      AND source_commit.message ~* '^[[:space:]]*Merge pull request #[1-9][0-9]*'
                  )
                GROUP BY repository.id, repository.full_name
                ORDER BY COUNT(*) DESC, repository.full_name;
                """;
            await using var reviewReader = await reviewCommand.ExecuteReaderAsync(ct);
            while (await reviewReader.ReadAsync(ct))
            {
                reviewRepositories.Add(new KnowledgeReviewRepositoryDto(
                    reviewReader.GetInt64(0),
                    reviewReader.GetString(1),
                    reviewReader.GetInt32(2)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        var knowledgeQuality = new KnowledgeQualitySummaryDto(
            reviewRepositories.Sum(item => item.ReviewLinks),
            reviewRepositories.Count,
            reviewRepositories);

        var activeRepositoryIds = repositories
            .Where(item => item.IsSyncEnabled && !item.IsExcluded)
            .Select(item => item.Id)
            .ToHashSet();
        var activeSyncStates = syncStates
            .Where(state => activeRepositoryIds.Contains(state.RepositoryId))
            .ToList();
        var staleThreshold = DateTime.UtcNow.AddDays(
            -Math.Max(1, app.Configuration.GetValue<int?>("Dashboard:StaleAfterDays") ?? 7));
        var staleResourceCount = activeSyncStates.Count(state =>
            !state.LastSuccessfulSync.HasValue || state.LastSuccessfulSync < staleThreshold);
        var trackedRepositoryIds = activeSyncStates.Select(state => state.RepositoryId).ToHashSet();
        var untrackedRepositoryCount = activeRepositoryIds.Count(id => !trackedRepositoryIds.Contains(id));
        var hasFailure = activeSyncStates.Any(state =>
            state.Status.Equals("failed", StringComparison.OrdinalIgnoreCase));
        var isRunning = activeSyncStates.Any(state =>
                state.Status.Equals("running", StringComparison.OrdinalIgnoreCase)) ||
            recentRuns.Any(run =>
                run.Status.Equals("running", StringComparison.OrdinalIgnoreCase) &&
                run.CompletedAt is null &&
                (!run.RepositoryId.HasValue || activeRepositoryIds.Contains(run.RepositoryId.Value)));
        var needsAttention = hasFailure || staleResourceCount > 0 || untrackedRepositoryCount > 0;

        var status = needsAttention ? "attention" : isRunning ? "running" : repositoryCount > 0 ? "healthy" : "empty";
        var trackingStatus = syncStates.Count == 0
            ? "not_initialized"
            : needsAttention ? "attention" : isRunning ? "running" : "active";

        var syncOverview = new SyncOverviewDto(
            activeSyncStates.Count,
            activeSyncStates.Count(state => state.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)),
            activeSyncStates.Count(state => state.Status.Equals("failed", StringComparison.OrdinalIgnoreCase)),
            activeSyncStates.Count(state => state.Status.Equals("running", StringComparison.OrdinalIgnoreCase)),
            activeSyncStates.Count(state => state.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)),
            staleResourceCount,
            untrackedRepositoryCount,
            totalRunCount,
            failedRunCount,
            recentRuns.FirstOrDefault()?.StartedAt);

        var totalRecords = repositoryCount + userCount + issueCount + commentCount +
            pullRequestCount + commitCount + pullRequestFileCount + commitFileCount +
            pullRequestCommitCount + repositoryFileRecordCount;

        return Results.Ok(new DashboardDto(
            DateTime.UtcNow,
            status,
            trackingStatus,
            latestDataSync,
            new TotalsDto(
                totalRecords,
                repositoryCount,
                activeRepositoryCount,
                pausedRepositoryCount,
                excludedRepositoryCount,
                userCount,
                issueCount,
                openIssueCount,
                commentCount,
                pullRequestCount,
                openPullRequestCount,
                mergedPullRequestCount,
                commitCount,
                pullRequestFileCount + commitFileCount,
                activeSourceFileCount,
                contentChunkCount),
            syncOverview,
            knowledgeQuality,
            repositories,
            syncStates,
            recentRuns));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Errore durante la lettura dei dati della dashboard.");
        return Results.Problem(
            title: "Database non raggiungibile",
            detail: "La dashboard non riesce a leggere PostgreSQL. Verifica rete e configurazione locale.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/search", async (
    string? q,
    long? repositoryId,
    string? entityType,
    int? page,
    int? pageSize,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var normalizedQuery = q?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedQuery.Length < 2)
        {
            return Results.BadRequest(new { message = "Inserisci almeno due caratteri da cercare." });
        }
        if (normalizedQuery.Length > 200)
        {
            return Results.BadRequest(new { message = "La ricerca non può superare 200 caratteri." });
        }
        if (repositoryId <= 0)
        {
            return Results.BadRequest(new { message = "Repository non valido." });
        }

        var normalizedEntityType = entityType?.Trim().ToLowerInvariant();
        var allowedEntityTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "repository", "issue", "issue_comment", "pull_request", "commit", "repository_file", "code_symbol"
        };
        if (!string.IsNullOrWhiteSpace(normalizedEntityType) && !allowedEntityTypes.Contains(normalizedEntityType))
        {
            return Results.BadRequest(new { message = "Tipo di risultato non valido." });
        }

        var normalizedPage = page is > 0 ? page.GetValueOrDefault() : 1;
        var normalizedPageSize = pageSize is > 0 ? Math.Min(pageSize.GetValueOrDefault(), 50) : 20;

        const string searchSql = """
            WITH search_query AS
            (
                SELECT websearch_to_tsquery('simple', @search_query) AS value
            ),
            candidates AS
            (
                SELECT
                    'repository'::text AS entity_type,
                    repository.id AS entity_id,
                    repository.id AS repository_id,
                    repository.full_name AS repository_full_name,
                    repository.full_name AS title,
                    repository.full_name || ' ' || COALESCE(repository.description, '') AS search_text,
                    COALESCE(repository.description, 'Repository GitHub') AS fallback_snippet,
                    repository.html_url,
                    NULL::text AS source_path,
                    NULL::integer AS source_line,
                    COALESCE(repository.updated_at, repository.pushed_at, repository.synced_at) AS updated_at,
                    (ts_rank_cd(
                        setweight(to_tsvector('simple', repository.full_name), 'A') ||
                        setweight(to_tsvector('simple', COALESCE(repository.description, '')), 'B'),
                        search_query.value,
                        32
                    ) + CASE WHEN repository.full_name ILIKE '%' || @raw_query || '%' THEN 0.35 ELSE 0 END)::double precision AS relevance
                FROM github.repositories AS repository
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND (CAST(@repository_id AS bigint) IS NULL OR repository.id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'repository')
                  AND (
                      setweight(to_tsvector('simple', repository.full_name), 'A') ||
                      setweight(to_tsvector('simple', COALESCE(repository.description, '')), 'B')
                  ) @@ search_query.value

                UNION ALL

                SELECT
                    'issue', issue.id, issue.repository_id, repository.full_name,
                    '#' || issue.number || ' · ' || issue.title,
                    issue.title || ' ' || COALESCE(issue.body, ''),
                    issue.title,
                    issue.html_url,
                    NULL::text,
                    NULL::integer,
                    issue.updated_at,
                    (ts_rank_cd(
                        setweight(to_tsvector('simple', issue.title), 'A') ||
                        setweight(to_tsvector('simple', COALESCE(issue.body, '')), 'B'),
                        search_query.value,
                        32
                    ) + CASE WHEN issue.title ILIKE '%' || @raw_query || '%' THEN 0.30 ELSE 0 END)::double precision
                FROM github.issues AS issue
                INNER JOIN github.repositories AS repository ON repository.id = issue.repository_id
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND (CAST(@repository_id AS bigint) IS NULL OR issue.repository_id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'issue')
                  AND (
                      setweight(to_tsvector('simple', issue.title), 'A') ||
                      setweight(to_tsvector('simple', COALESCE(issue.body, '')), 'B')
                  ) @@ search_query.value

                UNION ALL

                SELECT
                    'issue_comment', comment.id, comment.repository_id, repository.full_name,
                    'Commento su #' || issue.number || ' · ' || issue.title,
                    COALESCE(comment.body, ''),
                    'Commento su issue #' || issue.number,
                    comment.html_url,
                    NULL::text,
                    NULL::integer,
                    comment.updated_at,
                    ts_rank_cd(to_tsvector('simple', COALESCE(comment.body, '')), search_query.value, 32)::double precision
                FROM github.issue_comments AS comment
                INNER JOIN github.issues AS issue ON issue.id = comment.issue_id
                INNER JOIN github.repositories AS repository ON repository.id = comment.repository_id
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND (CAST(@repository_id AS bigint) IS NULL OR comment.repository_id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'issue_comment')
                  AND to_tsvector('simple', COALESCE(comment.body, '')) @@ search_query.value

                UNION ALL

                SELECT
                    'pull_request', pull_request.id, pull_request.repository_id, repository.full_name,
                    'PR #' || pull_request.number || ' · ' || pull_request.title,
                    pull_request.title || ' ' || COALESCE(pull_request.body, ''),
                    pull_request.title,
                    pull_request.html_url,
                    NULL::text,
                    NULL::integer,
                    pull_request.updated_at,
                    (ts_rank_cd(
                        setweight(to_tsvector('simple', pull_request.title), 'A') ||
                        setweight(to_tsvector('simple', COALESCE(pull_request.body, '')), 'B'),
                        search_query.value,
                        32
                    ) + CASE WHEN pull_request.title ILIKE '%' || @raw_query || '%' THEN 0.30 ELSE 0 END)::double precision
                FROM github.pull_requests AS pull_request
                INNER JOIN github.repositories AS repository ON repository.id = pull_request.repository_id
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND (CAST(@repository_id AS bigint) IS NULL OR pull_request.repository_id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'pull_request')
                  AND (
                      setweight(to_tsvector('simple', pull_request.title), 'A') ||
                      setweight(to_tsvector('simple', COALESCE(pull_request.body, '')), 'B')
                  ) @@ search_query.value

                UNION ALL

                SELECT
                    'commit', commit.id, commit.repository_id, repository.full_name,
                    LEFT(commit.sha, 8) || ' · ' || SPLIT_PART(commit.message, E'\n', 1),
                    commit.sha || ' ' || commit.message || ' ' || COALESCE(commit.author_name, ''),
                    SPLIT_PART(commit.message, E'\n', 1),
                    commit.html_url,
                    NULL::text,
                    NULL::integer,
                    COALESCE(commit.committed_at, commit.authored_at, commit.synced_at),
                    (ts_rank_cd(
                        setweight(to_tsvector('simple', commit.sha), 'A') ||
                        setweight(to_tsvector('simple', commit.message), 'B') ||
                        setweight(to_tsvector('simple', COALESCE(commit.author_name, '')), 'C'),
                        search_query.value,
                        32
                    ) + CASE WHEN commit.sha ILIKE @raw_query || '%' THEN 0.40 ELSE 0 END)::double precision
                FROM github.commits AS commit
                INNER JOIN github.repositories AS repository ON repository.id = commit.repository_id
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND (CAST(@repository_id AS bigint) IS NULL OR commit.repository_id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'commit')
                  AND (
                      setweight(to_tsvector('simple', commit.sha), 'A') ||
                      setweight(to_tsvector('simple', commit.message), 'B') ||
                      setweight(to_tsvector('simple', COALESCE(commit.author_name, '')), 'C')
                  ) @@ search_query.value

                UNION ALL

                SELECT
                    'repository_file', file.id, file.repository_id, repository.full_name,
                    file.path,
                    file.path || ' ' || file.content,
                    'File sorgente · ' || COALESCE(file.language, file.extension, 'tipo non rilevato'),
                    file.html_url,
                    file.path,
                    (
                        SELECT source_line.ordinality::integer
                        FROM regexp_split_to_table(replace(file.content, E'\r\n', E'\n'), E'\n')
                            WITH ORDINALITY AS source_line(line, ordinality)
                        WHERE to_tsvector('simple', source_line.line) @@ search_query.value
                        LIMIT 1
                    ),
                    file.synced_at,
                    (ts_rank_cd(
                        setweight(to_tsvector('simple', COALESCE(file.path, '')), 'A') ||
                        setweight(to_tsvector('simple', COALESCE(file.content, '')), 'B'),
                        search_query.value,
                        32
                    ) + CASE WHEN file.path ILIKE '%' || @raw_query || '%' THEN 0.35 ELSE 0 END)::double precision
                FROM github.repository_files AS file
                INNER JOIN github.repositories AS repository ON repository.id = file.repository_id
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND NOT file.is_deleted
                  AND (CAST(@repository_id AS bigint) IS NULL OR file.repository_id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'repository_file')
                  AND (
                      setweight(to_tsvector('simple', COALESCE(file.path, '')), 'A') ||
                      setweight(to_tsvector('simple', COALESCE(file.content, '')), 'B')
                  ) @@ search_query.value

                UNION ALL

                SELECT
                    'code_symbol', symbol.id, symbol.repository_id, repository.full_name,
                    symbol.qualified_name,
                    symbol.qualified_name || ' ' || symbol.signature || ' ' || symbol.kind || ' ' || COALESCE(symbol.containing_symbol, ''),
                    symbol.kind || ' · ' || symbol.signature,
                    file.html_url || '#L' || symbol.start_line,
                    file.path,
                    symbol.start_line,
                    symbol.indexed_at,
                    (ts_rank_cd(
                        setweight(to_tsvector('simple', symbol.qualified_name), 'A') ||
                        setweight(to_tsvector('simple', symbol.signature), 'B') ||
                        setweight(to_tsvector('simple', symbol.kind), 'C'),
                        search_query.value,
                        32
                    ) + CASE WHEN symbol.qualified_name ILIKE '%' || @raw_query || '%' THEN 0.45 ELSE 0 END)::double precision
                FROM knowledge.code_symbols AS symbol
                INNER JOIN github.repository_files AS file ON file.id = symbol.repository_file_id
                INNER JOIN github.repositories AS repository ON repository.id = symbol.repository_id
                CROSS JOIN search_query
                WHERE NOT repository.is_excluded
                  AND NOT file.is_deleted
                  AND (CAST(@repository_id AS bigint) IS NULL OR symbol.repository_id = @repository_id)
                  AND (CAST(@entity_type AS text) IS NULL OR @entity_type = 'code_symbol')
                  AND (
                      setweight(to_tsvector('simple', symbol.qualified_name), 'A') ||
                      setweight(to_tsvector('simple', symbol.signature), 'B') ||
                      setweight(to_tsvector('simple', symbol.kind), 'C')
                  ) @@ search_query.value
            ),
            ranked AS MATERIALIZED
            (
                SELECT candidates.*, COUNT(*) OVER () AS matched_count
                FROM candidates
                ORDER BY relevance DESC, updated_at DESC, title
                LIMIT @page_size OFFSET @offset
            )
            SELECT
                ranked.entity_type,
                ranked.entity_id,
                ranked.repository_id,
                ranked.repository_full_name,
                ranked.title,
                CASE
                    WHEN ranked.search_text = '' THEN ranked.fallback_snippet
                    ELSE ts_headline(
                        'simple', ranked.search_text, search_query.value,
                        'StartSel=⟦, StopSel=⟧, MaxWords=32, MinWords=8, ShortWord=2, HighlightAll=false, MaxFragments=2, FragmentDelimiter=…'
                    )
                END AS snippet,
                ranked.html_url,
                ranked.source_path,
                ranked.source_line,
                ranked.updated_at,
                ranked.relevance,
                ranked.matched_count
            FROM ranked
            CROSS JOIN search_query
            ORDER BY ranked.relevance DESC, ranked.updated_at DESC, ranked.title;
            """;

        var results = new List<GlobalSearchResultDto>();
        var matchedResults = 0;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = searchSql;
            AddDbParameter(command, "search_query", normalizedQuery);
            AddDbParameter(command, "raw_query", normalizedQuery);
            AddDbParameter(command, "repository_id", repositoryId);
            AddDbParameter(command, "entity_type", string.IsNullOrWhiteSpace(normalizedEntityType) ? null : normalizedEntityType);
            AddDbParameter(command, "page_size", normalizedPageSize);
            AddDbParameter(command, "offset", checked((normalizedPage - 1) * normalizedPageSize));

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                matchedResults = checked((int)reader.GetInt64(11));
                results.Add(new GlobalSearchResultDto(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.GetDateTime(9),
                    reader.GetDouble(10)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return Results.Ok(new GlobalSearchCatalogDto(
            normalizedQuery,
            repositoryId,
            string.IsNullOrWhiteSpace(normalizedEntityType) ? null : normalizedEntityType,
            matchedResults,
            normalizedPage,
            normalizedPageSize,
            matchedResults == 0 ? 0 : (int)Math.Ceiling(matchedResults / (double)normalizedPageSize),
            results));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Errore durante la ricerca trasversale di {SearchQuery}.", q);
        return Results.Problem(
            title: "Ricerca non disponibile",
            detail: "La dashboard non riesce a completare la ricerca trasversale in PostgreSQL.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapPatch("/api/repositories/{id:long}/synchronization", async (
    long id,
    RepositorySynchronizationRequest request,
    HttpRequest httpRequest,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    if (!IsDashboardManagementRequest(httpRequest))
    {
        return Results.Problem(
            title: "Operazione non autorizzata",
            detail: "La richiesta di gestione non proviene dalla dashboard locale.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        await using var operationLock = await DashboardImportExecutionLock.TryAcquireAsync(db, ct);
        if (operationLock is null)
        {
            return ImportInProgressResult();
        }

        var repository = await db.Repositories.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (repository is null)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        if (repository.IsExcluded && request.Enabled)
        {
            return Results.Conflict(new
            {
                message = "Il repository è escluso. Ripristinalo prima di riattivare la sincronizzazione."
            });
        }

        var changedAt = DateTime.UtcNow;
        repository.IsSyncEnabled = request.Enabled;
        repository.SyncDisabledAt = request.Enabled ? null : changedAt;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new RepositoryControlDto(
            repository.Id,
            repository.FullName,
            repository.IsSyncEnabled,
            repository.SyncDisabledAt,
            repository.IsExcluded,
            repository.ExcludedAt));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la modifica della sincronizzazione del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Impostazione non salvata",
            detail: "La dashboard non è riuscita ad aggiornare lo stato del repository.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapPost("/api/repositories/{id:long}/restore", async (
    long id,
    HttpRequest httpRequest,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    if (!IsDashboardManagementRequest(httpRequest))
    {
        return Results.Problem(
            title: "Operazione non autorizzata",
            detail: "La richiesta di gestione non proviene dalla dashboard locale.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        await using var operationLock = await DashboardImportExecutionLock.TryAcquireAsync(db, ct);
        if (operationLock is null)
        {
            return ImportInProgressResult();
        }

        var repository = await db.Repositories.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (repository is null)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        repository.IsExcluded = false;
        repository.ExcludedAt = null;
        repository.IsSyncEnabled = true;
        repository.SyncDisabledAt = null;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new RepositoryControlDto(
            repository.Id,
            repository.FullName,
            repository.IsSyncEnabled,
            repository.SyncDisabledAt,
            repository.IsExcluded,
            repository.ExcludedAt));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante il ripristino del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Repository non ripristinato",
            detail: "La dashboard non è riuscita a ripristinare il repository.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapDelete("/api/repositories/{id:long}", async (
    long id,
    [FromBody] RepositoryDeleteRequest request,
    HttpRequest httpRequest,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    if (!IsDashboardManagementRequest(httpRequest))
    {
        return Results.Problem(
            title: "Operazione non autorizzata",
            detail: "La richiesta di gestione non proviene dalla dashboard locale.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    try
    {
        await using var operationLock = await DashboardImportExecutionLock.TryAcquireAsync(db, ct);
        if (operationLock is null)
        {
            return ImportInProgressResult();
        }

        var repository = await db.Repositories.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (repository is null)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        if (!string.Equals(
                repository.FullName,
                request.RepositoryFullName?.Trim(),
                StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                message = "Conferma non valida: il nome del repository non corrisponde."
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var deletedStructuralRows = await CountStructuralRowsAsync(db, id, ct);
        var deletedLinks = await db.PullRequestCommits
            .Where(item => item.PullRequest.RepositoryId == id || item.Commit.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedPullRequestFiles = await db.PullRequestFiles
            .Where(item => item.PullRequest.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedCommitFiles = await db.CommitFiles
            .Where(item => item.Commit.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedComments = await db.IssueComments
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedSourceFiles = await db.RepositoryFiles
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedIssues = await db.Issues
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedPullRequests = await db.PullRequests
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedCommits = await db.Commits
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedSyncStates = await db.SyncStates
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);
        var deletedSyncRuns = await db.SyncRuns
            .Where(item => item.RepositoryId == id)
            .ExecuteDeleteAsync(ct);

        var changedAt = DateTime.UtcNow;
        repository.IsSyncEnabled = false;
        repository.SyncDisabledAt = changedAt;
        repository.IsExcluded = true;
        repository.ExcludedAt = changedAt;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var deletedRecords = deletedLinks + deletedPullRequestFiles + deletedCommitFiles +
            deletedComments + deletedSourceFiles + deletedIssues + deletedPullRequests +
            deletedCommits + deletedSyncStates + deletedSyncRuns + deletedStructuralRows;

        return Results.Ok(new RepositoryDeletionResultDto(
            repository.Id,
            repository.FullName,
            changedAt,
            deletedRecords));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la cancellazione dei dati del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Dati non cancellati",
            detail: "La dashboard non è riuscita a cancellare i dati del repository.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}", async (
    long id,
    string? q,
    string? state,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var repository = await db.Repositories.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new RepositoryDetailDto(
                item.Id,
                item.FullName,
                item.Description,
                item.HtmlUrl,
                item.IsPrivate,
                item.IsArchived,
                item.IsSyncEnabled,
                item.SyncDisabledAt,
                item.IsExcluded,
                item.ExcludedAt,
                item.DefaultBranch,
                item.PrimaryLanguage,
                item.CreatedAt,
                item.PushedAt,
                item.SyncedAt,
                item.Issues.Count,
                item.Issues.Count(issue => issue.State == "open"),
                item.PullRequests.Count,
                item.PullRequests.Count(pullRequest => pullRequest.State == "open"),
                item.PullRequests.Count(pullRequest => pullRequest.Merged),
                item.Commits.Count,
                item.Commits.SelectMany(commit => commit.CommitFiles).Count(),
                item.RepositoryFiles.Count(file => !file.IsDeleted)))
            .SingleOrDefaultAsync(ct);

        if (repository is null)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        var normalizedQuery = q?.Trim();
        var normalizedState = state?.Trim().ToLowerInvariant();
        if (normalizedQuery?.Length > 200)
        {
            return Results.BadRequest(new { message = "La ricerca non può superare 200 caratteri." });
        }

        if (normalizedState == "all")
        {
            normalizedState = null;
        }
        else if (normalizedState is not null and not "open" and not "closed" and not "merged")
        {
            return Results.BadRequest(new { message = "Filtro di stato non valido." });
        }

        var issuesQuery = db.Issues.AsNoTracking().Where(item => item.RepositoryId == id);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            issuesQuery = issuesQuery.Where(item =>
                EF.Functions.ILike(item.Title, $"%{normalizedQuery}%") ||
                (item.Body != null && EF.Functions.ILike(item.Body, $"%{normalizedQuery}%")) ||
                (item.AuthorUser != null && EF.Functions.ILike(item.AuthorUser.Login, $"%{normalizedQuery}%")));
        }
        if (normalizedState is "open" or "closed")
        {
            issuesQuery = issuesQuery.Where(item => item.State == normalizedState);
        }
        else if (normalizedState == "merged")
        {
            issuesQuery = issuesQuery.Where(_ => false);
        }

        var pullRequestsQuery = db.PullRequests.AsNoTracking().Where(item => item.RepositoryId == id);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            pullRequestsQuery = pullRequestsQuery.Where(item =>
                EF.Functions.ILike(item.Title, $"%{normalizedQuery}%") ||
                (item.Body != null && EF.Functions.ILike(item.Body, $"%{normalizedQuery}%")) ||
                (item.AuthorUser != null && EF.Functions.ILike(item.AuthorUser.Login, $"%{normalizedQuery}%")));
        }
        if (normalizedState == "merged")
        {
            pullRequestsQuery = pullRequestsQuery.Where(item => item.Merged);
        }
        else if (normalizedState is "open" or "closed")
        {
            pullRequestsQuery = pullRequestsQuery.Where(item => item.State == normalizedState);
        }

        var commitsQuery = db.Commits.AsNoTracking().Where(item => item.RepositoryId == id);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            commitsQuery = commitsQuery.Where(item =>
                EF.Functions.ILike(item.Message, $"%{normalizedQuery}%") ||
                EF.Functions.ILike(item.Sha, $"%{normalizedQuery}%") ||
                (item.AuthorName != null && EF.Functions.ILike(item.AuthorName, $"%{normalizedQuery}%")) ||
                (item.AuthorUser != null && EF.Functions.ILike(item.AuthorUser.Login, $"%{normalizedQuery}%")));
        }

        var filesQuery = db.CommitFiles.AsNoTracking()
            .Where(item => item.Commit.RepositoryId == id);
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            filesQuery = filesQuery.Where(item => EF.Functions.ILike(item.Filename, $"%{normalizedQuery}%"));
        }

        var issueResultCount = await issuesQuery.CountAsync(ct);
        var pullRequestResultCount = await pullRequestsQuery.CountAsync(ct);
        var commitResultCount = await commitsQuery.CountAsync(ct);
        var fileResultCount = await filesQuery
            .Select(item => item.Filename)
            .Distinct()
            .CountAsync(ct);

        var issues = await issuesQuery
            .OrderByDescending(item => item.UpdatedAt)
            .Take(75)
            .Select(item => new IssueExplorerDto(
                item.Id, item.Number, item.Title, item.State,
                item.AuthorUser != null ? item.AuthorUser.Login : null,
                item.CommentsCount, item.CreatedAt, item.UpdatedAt, item.ClosedAt, item.HtmlUrl))
            .ToListAsync(ct);

        var pullRequests = await pullRequestsQuery
            .OrderByDescending(item => item.UpdatedAt)
            .Take(75)
            .Select(item => new PullRequestExplorerDto(
                item.Id, item.Number, item.Title, item.State, item.Merged, item.IsDraft,
                item.AuthorUser != null ? item.AuthorUser.Login : null,
                item.BaseBranch, item.HeadBranch, item.CommitsCount, item.ChangedFiles,
                item.Additions, item.Deletions, item.CreatedAt, item.UpdatedAt, item.MergedAt, item.HtmlUrl))
            .ToListAsync(ct);

        var commits = await commitsQuery
            .OrderByDescending(item => item.CommittedAt ?? item.AuthoredAt ?? item.SyncedAt)
            .Take(75)
            .Select(item => new CommitExplorerDto(
                item.Id, item.Sha, item.Message,
                item.AuthorUser != null ? item.AuthorUser.Login : item.AuthorName,
                item.AuthoredAt, item.CommittedAt, item.Additions, item.Deletions,
                item.FilesChanged, item.HtmlUrl))
            .ToListAsync(ct);

        var fileRows = await filesQuery
            .Select(item => new
            {
                item.Filename,
                Additions = item.Additions ?? 0,
                Deletions = item.Deletions ?? 0,
                Changes = item.Changes ?? 0,
                LastTouchedAt = item.Commit.CommittedAt ?? item.Commit.AuthoredAt ?? item.Commit.SyncedAt
            })
            .GroupBy(item => item.Filename)
            .Select(group => new
            {
                Filename = group.Key,
                Touches = group.Count(),
                Additions = group.Sum(item => item.Additions),
                Deletions = group.Sum(item => item.Deletions),
                Changes = group.Sum(item => item.Changes),
                LastTouchedAt = (DateTime?)group.Max(item => item.LastTouchedAt)
            })
            .OrderByDescending(item => item.Touches)
            .ThenBy(item => item.Filename)
            .Take(100)
            .ToListAsync(ct);
        var files = fileRows
            .Select(item => new FileExplorerDto(
                item.Filename,
                item.Touches,
                item.Additions,
                item.Deletions,
                item.Changes,
                item.LastTouchedAt))
            .ToList();

        var syncStates = await db.SyncStates.AsNoTracking()
            .Where(item => item.RepositoryId == id)
            .OrderBy(item => item.ResourceType)
            .Select(item => new RepositorySyncStateDto(
                item.ResourceType, item.Status, item.LastSuccessfulSync,
                item.LastGithubUpdatedAt, item.UpdatedAt))
            .ToListAsync(ct);

        var repositoryOptions = await db.Repositories.AsNoTracking()
            .Where(item => item.Issues.Any() || item.PullRequests.Any() ||
                item.Commits.Any() || item.RepositoryFiles.Any(file => !file.IsDeleted))
            .OrderBy(item => item.FullName)
            .Select(item => new RepositoryOptionDto(item.Id, item.FullName))
            .ToListAsync(ct);

        return Results.Ok(new RepositoryExplorerDto(
            DateTime.UtcNow,
            repository,
            repositoryOptions,
            syncStates,
            issues,
            pullRequests,
            commits,
            files,
            new ExplorerResultCountsDto(
                issueResultCount,
                pullRequestResultCount,
                commitResultCount,
                fileResultCount,
                repository.SourceFiles)));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Errore durante l'esplorazione del repository {RepositoryId}.", id);
        return Results.Problem(
            title: "Repository non disponibile",
            detail: "La dashboard non riesce a leggere i dati del repository da PostgreSQL.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}/source", async (
    long id,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var repository = await db.Repositories.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                item.Id,
                item.FullName,
                item.DefaultBranch
            })
            .SingleOrDefaultAsync(ct);

        if (repository is null)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        var files = await db.RepositoryFiles.AsNoTracking()
            .Where(item => item.RepositoryId == id && !item.IsDeleted)
            .OrderBy(item => item.Path)
            .Select(item => new SourceFileSummaryDto(
                item.Path,
                item.FileName,
                item.Extension,
                item.Language,
                item.Branch,
                item.BlobSha,
                item.SizeBytes,
                item.LineCount,
                item.SyncedAt))
            .ToListAsync(ct);

        var directoryCount = files
            .SelectMany(file =>
            {
                var parts = file.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                return Enumerable.Range(1, Math.Max(0, parts.Length - 1))
                    .Select(depth => string.Join("/", parts.Take(depth)));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var languages = files
            .GroupBy(file => file.Language ?? "Altro", StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceFacetDto(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .ToList();

        var extensions = files
            .Where(file => !string.IsNullOrWhiteSpace(file.Extension))
            .GroupBy(file => file.Extension!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new SourceFacetDto(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .ToList();

        return Results.Ok(new SourceCatalogDto(
            repository.Id,
            repository.FullName,
            repository.DefaultBranch,
            files.Count,
            directoryCount,
            files.Sum(file => (long)file.LineCount),
            files.Sum(file => file.SizeBytes),
            files.Count == 0 ? null : files.Max(file => file.SyncedAt),
            languages,
            extensions,
            files));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la lettura del catalogo sorgente del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Codice sorgente non disponibile",
            detail: "La dashboard non riesce a leggere il catalogo dei file da PostgreSQL.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}/source/file", async (
    long id,
    string? path,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var normalizedPath = path?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPath) || normalizedPath.Length > 2048)
        {
            return Results.BadRequest(new { message = "Percorso file non valido." });
        }

        var file = await db.RepositoryFiles.AsNoTracking()
            .Where(item => item.RepositoryId == id &&
                !item.IsDeleted && item.Path == normalizedPath)
            .Select(item => new SourceFileContentDto(
                item.Path,
                item.FileName,
                item.Extension,
                item.Language,
                item.Branch,
                item.BlobSha,
                item.SizeBytes,
                item.LineCount,
                item.ContentEncoding,
                item.Content,
                item.HtmlUrl,
                item.SyncedAt))
            .SingleOrDefaultAsync(ct);

        return file is null
            ? Results.NotFound(new { message = "File non trovato o non più attivo." })
            : Results.Ok(file);
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la lettura del file {Path} del repository {RepositoryId}.",
            path,
            id);
        return Results.Problem(
            title: "File non disponibile",
            detail: "La dashboard non riesce a leggere il contenuto del file da PostgreSQL.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}/source/search", async (
    long id,
    string? q,
    string? extension,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var normalizedQuery = q?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedQuery) || normalizedQuery.Length < 2)
        {
            return Results.BadRequest(new { message = "Inserisci almeno due caratteri da cercare." });
        }

        if (normalizedQuery.Length > 200)
        {
            return Results.BadRequest(new { message = "La ricerca non può superare 200 caratteri." });
        }

        var repositoryExists = await db.Repositories.AsNoTracking()
            .AnyAsync(item => item.Id == id, ct);
        if (!repositoryExists)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        var normalizedExtension = extension?.Trim().ToLowerInvariant();
        if (normalizedExtension?.Length > 30)
        {
            return Results.BadRequest(new { message = "Estensione file non valida." });
        }

        const string searchVectorSql = """
            setweight(
                to_tsvector('simple', COALESCE(r.path, '')),
                'A'
            ) ||
            setweight(
                to_tsvector('simple', COALESCE(r.content, '')),
                'B'
            )
            """;

        var commandText = $$"""
            WITH search_query AS
            (
                SELECT websearch_to_tsquery('simple', @search_query) AS value
            ),
            ranked AS
            (
                SELECT
                    r.path,
                    r.language,
                    r.extension,
                    r.content,
                    to_tsvector('simple', COALESCE(r.content, '')) @@ search_query.value
                        AS content_match,
                    ts_rank_cd(({{searchVectorSql}}), search_query.value, 32)
                        AS relevance,
                    COUNT(*) OVER () AS total_count
                FROM github.repository_files AS r
                CROSS JOIN search_query
                WHERE r.repository_id = @repository_id
                  AND NOT r.is_deleted
                  AND (CAST(@extension AS text) IS NULL OR r.extension = @extension)
                  AND ({{searchVectorSql}}) @@ search_query.value
                ORDER BY relevance DESC, r.path
                LIMIT 100
            )
            SELECT
                ranked.path,
                ranked.language,
                ranked.extension,
                (
                    SELECT source_line.ordinality::integer
                    FROM regexp_split_to_table(
                        replace(ranked.content, E'\r\n', E'\n'),
                        E'\n'
                    ) WITH ORDINALITY AS source_line(line, ordinality)
                    WHERE to_tsvector('simple', source_line.line) @@ search_query.value
                    LIMIT 1
                ) AS line_number,
                CASE WHEN ranked.content_match THEN 'content' ELSE 'path' END
                    AS match_type,
                CASE
                    WHEN ranked.content_match THEN ts_headline(
                        'simple',
                        ranked.content,
                        search_query.value,
                        'StartSel=⟦, StopSel=⟧, MaxWords=28, MinWords=8, ShortWord=2, HighlightAll=false, MaxFragments=2, FragmentDelimiter=…'
                    )
                    ELSE 'Percorso: ' || ranked.path
                END AS snippet,
                ranked.relevance,
                ranked.total_count
            FROM ranked
            CROSS JOIN search_query
            ORDER BY ranked.relevance DESC, ranked.path;
            """;

        var matches = new List<SourceSearchMatchDto>();
        var matchedFiles = 0;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = commandText;

            var repositoryParameter = command.CreateParameter();
            repositoryParameter.ParameterName = "repository_id";
            repositoryParameter.Value = id;
            command.Parameters.Add(repositoryParameter);

            var searchParameter = command.CreateParameter();
            searchParameter.ParameterName = "search_query";
            searchParameter.Value = normalizedQuery;
            command.Parameters.Add(searchParameter);

            var extensionParameter = command.CreateParameter();
            extensionParameter.ParameterName = "extension";
            extensionParameter.Value = string.IsNullOrWhiteSpace(normalizedExtension)
                ? DBNull.Value
                : normalizedExtension;
            command.Parameters.Add(extensionParameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                matchedFiles = checked((int)reader.GetInt64(7));
                var snippet = reader.GetString(5).Trim();
                if (snippet.Length > 420)
                {
                    snippet = $"{snippet[..420]}…";
                }

                matches.Add(new SourceSearchMatchDto(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.GetString(4),
                    snippet,
                    reader.GetFloat(6)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return Results.Ok(new SourceSearchResultDto(
            normalizedQuery,
            normalizedExtension,
            matchedFiles,
            matchedFiles > matches.Count,
            "postgresql_full_text",
            matches));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la ricerca nel codice del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Ricerca non disponibile",
            detail: "La dashboard non riesce a cercare nel codice sorgente.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}/source/symbols", async (
    long id,
    string? q,
    string? kind,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var normalizedQuery = q?.Trim();
        if (normalizedQuery?.Length > 200)
        {
            return Results.BadRequest(new { message = "La ricerca non può superare 200 caratteri." });
        }

        var allowedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "namespace", "class", "interface", "enum", "struct", "record",
            "method", "constructor", "property"
        };
        var normalizedKind = kind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedKind) && !allowedKinds.Contains(normalizedKind))
        {
            return Results.BadRequest(new { message = "Tipo di simbolo C# non valido." });
        }

        var repositoryExists = await db.Repositories.AsNoTracking()
            .AnyAsync(item => item.Id == id, ct);
        if (!repositoryExists)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            int csharpFiles;
            int indexedFiles;
            int partialFiles;
            int symbolCount;
            DateTime? lastIndexedAt;

            await using (var overviewCommand = connection.CreateCommand())
            {
                overviewCommand.CommandText = """
                    SELECT
                        (
                            SELECT COUNT(*)::integer
                            FROM github.repository_files AS source
                            WHERE source.repository_id = @repository_id
                              AND NOT source.is_deleted
                              AND source.extension = '.cs'
                        ) AS csharp_files,
                        COUNT(*)::integer AS indexed_files,
                        COUNT(*) FILTER (WHERE file_index.status = 'partial')::integer
                            AS partial_files,
                        COALESCE(SUM(file_index.symbol_count), 0)::integer AS symbol_count,
                        MAX(file_index.indexed_at) AS last_indexed_at
                    FROM knowledge.code_file_indexes AS file_index
                    WHERE file_index.repository_id = @repository_id;
                    """;
                AddDbParameter(overviewCommand, "repository_id", id);
                await using var reader = await overviewCommand.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                csharpFiles = reader.GetInt32(0);
                indexedFiles = reader.GetInt32(1);
                partialFiles = reader.GetInt32(2);
                symbolCount = reader.GetInt32(3);
                lastIndexedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
            }

            var facets = new List<SourceFacetDto>();
            await using (var facetCommand = connection.CreateCommand())
            {
                facetCommand.CommandText = """
                    SELECT symbol.kind, COUNT(*)::integer
                    FROM knowledge.code_symbols AS symbol
                    WHERE symbol.repository_id = @repository_id
                    GROUP BY symbol.kind
                    ORDER BY COUNT(*) DESC, symbol.kind;
                    """;
                AddDbParameter(facetCommand, "repository_id", id);
                await using var reader = await facetCommand.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    facets.Add(new SourceFacetDto(reader.GetString(0), reader.GetInt32(1)));
                }
            }

            const string symbolsSql = """
                SELECT
                    symbol.kind,
                    symbol.name,
                    symbol.qualified_name,
                    symbol.signature,
                    symbol.containing_symbol,
                    symbol.accessibility,
                    symbol.modifiers,
                    symbol.return_type,
                    symbol.start_line,
                    symbol.end_line,
                    source.path,
                    COUNT(*) OVER () AS total_count
                FROM knowledge.code_symbols AS symbol
                INNER JOIN github.repository_files AS source
                    ON source.id = symbol.repository_file_id
                WHERE symbol.repository_id = @repository_id
                  AND NOT source.is_deleted
                  AND (CAST(@kind AS text) IS NULL OR symbol.kind = @kind)
                  AND
                  (
                      CAST(@query AS text) IS NULL
                      OR POSITION(LOWER(@query) IN LOWER(symbol.name)) > 0
                      OR POSITION(LOWER(@query) IN LOWER(symbol.qualified_name)) > 0
                      OR POSITION(LOWER(@query) IN LOWER(symbol.signature)) > 0
                  )
                ORDER BY
                    CASE WHEN LOWER(symbol.name) = LOWER(@query) THEN 0 ELSE 1 END,
                    CASE WHEN POSITION(LOWER(@query) IN LOWER(symbol.name)) = 1 THEN 0 ELSE 1 END,
                    symbol.kind,
                    symbol.qualified_name,
                    symbol.signature,
                    symbol.start_line
                LIMIT 250;
                """;

            var symbols = new List<CSharpSymbolDto>();
            var matchedSymbols = 0;
            await using (var symbolsCommand = connection.CreateCommand())
            {
                symbolsCommand.CommandText = symbolsSql;
                AddDbParameter(symbolsCommand, "repository_id", id);
                AddDbParameter(
                    symbolsCommand,
                    "kind",
                    string.IsNullOrWhiteSpace(normalizedKind) ? null : normalizedKind);
                AddDbParameter(
                    symbolsCommand,
                    "query",
                    string.IsNullOrWhiteSpace(normalizedQuery) ? null : normalizedQuery);

                await using var reader = await symbolsCommand.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    matchedSymbols = checked((int)reader.GetInt64(11));
                    symbols.Add(new CSharpSymbolDto(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.GetFieldValue<string[]>(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetString(10)));
                }
            }

            return Results.Ok(new CSharpSymbolCatalogDto(
                id,
                csharpFiles,
                indexedFiles,
                partialFiles,
                symbolCount,
                matchedSymbols,
                matchedSymbols > symbols.Count,
                lastIndexedAt,
                normalizedQuery,
                normalizedKind,
                facets,
                symbols));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la lettura dei simboli C# del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Struttura C# non disponibile",
            detail: "La dashboard non riesce a leggere l'indice strutturale da PostgreSQL.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}/chunks", async (
    long id,
    string? q,
    string? sourceType,
    int? page,
    int? pageSize,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        if (!await db.Repositories.AsNoTracking().AnyAsync(item => item.Id == id, ct))
            return Results.NotFound(new { message = "Repository non trovato." });

        var normalizedQuery = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var normalizedType = string.IsNullOrWhiteSpace(sourceType) ||
            sourceType.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : sourceType.Trim().ToLowerInvariant();
        var allowedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "issue", "issue_comment", "pull_request", "commit",
            "repository_file", "code_symbol"
        };
        if (normalizedType is not null && !allowedTypes.Contains(normalizedType))
            return Results.BadRequest(new { message = "Tipo di fonte non valido." });

        var currentPage = Math.Max(1, page ?? 1);
        var currentPageSize = Math.Clamp(pageSize ?? 30, 1, 50);
        var offset = (currentPage - 1) * currentPageSize;
        var chunks = new List<ContentChunkDto>();
        var facets = new List<ContentChunkFacetDto>();
        var totalChunks = 0;
        var totalSources = 0;
        long totalCharacters = 0;
        long estimatedTokens = 0;
        var matchedChunks = 0;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using (var summaryCommand = db.Database.GetDbConnection().CreateCommand())
            {
                summaryCommand.CommandText = """
                    SELECT
                        COUNT(*)::integer,
                        COUNT(DISTINCT chunk_source_id)::integer,
                        COALESCE(SUM(character_count), 0)::bigint,
                        COALESCE(SUM(estimated_tokens), 0)::bigint
                    FROM knowledge.content_chunks
                    WHERE repository_id = @repository_id;
                    """;
                AddDbParameter(summaryCommand, "repository_id", id);
                await using var summaryReader = await summaryCommand.ExecuteReaderAsync(ct);
                if (await summaryReader.ReadAsync(ct))
                {
                    totalChunks = summaryReader.GetInt32(0);
                    totalSources = summaryReader.GetInt32(1);
                    totalCharacters = summaryReader.GetInt64(2);
                    estimatedTokens = summaryReader.GetInt64(3);
                }
            }

            await using (var facetCommand = db.Database.GetDbConnection().CreateCommand())
            {
                facetCommand.CommandText = """
                    SELECT source.source_type, COUNT(*)::integer
                    FROM knowledge.content_chunks AS chunk
                    INNER JOIN knowledge.chunk_sources AS source
                        ON source.id = chunk.chunk_source_id
                    WHERE chunk.repository_id = @repository_id
                    GROUP BY source.source_type
                    ORDER BY source.source_type;
                    """;
                AddDbParameter(facetCommand, "repository_id", id);
                await using var facetReader = await facetCommand.ExecuteReaderAsync(ct);
                while (await facetReader.ReadAsync(ct))
                {
                    facets.Add(new ContentChunkFacetDto(
                        facetReader.GetString(0),
                        facetReader.GetInt32(1)));
                }
            }

            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                WITH search_query AS
                (
                    SELECT CASE
                        WHEN CAST(@query AS text) IS NULL THEN NULL
                        ELSE websearch_to_tsquery('simple', @query)
                    END AS value
                ),
                filtered AS
                (
                    SELECT
                        chunk.id,
                        source.source_type,
                        source.source_entity_id,
                        source.title,
                        source.source_path,
                        COALESCE(chunk.start_line, source.start_line) AS start_line,
                        COALESCE(chunk.end_line, source.end_line) AS end_line,
                        source.html_url,
                        chunk.ordinal,
                        chunk.content,
                        chunk.character_count,
                        chunk.estimated_tokens,
                        chunk.indexed_at,
                        CASE
                            WHEN search_query.value IS NULL THEN 0::double precision
                            ELSE ts_rank_cd(
                                to_tsvector('simple', chunk.content),
                                search_query.value,
                                32
                            )::double precision
                        END AS relevance
                    FROM knowledge.content_chunks AS chunk
                    INNER JOIN knowledge.chunk_sources AS source
                        ON source.id = chunk.chunk_source_id
                    CROSS JOIN search_query
                    WHERE chunk.repository_id = @repository_id
                      AND (CAST(@source_type AS text) IS NULL OR source.source_type = @source_type)
                      AND
                      (
                          search_query.value IS NULL OR
                          to_tsvector('simple', chunk.content) @@ search_query.value OR
                          source.title ILIKE '%' || @query || '%' OR
                          COALESCE(source.source_path, '') ILIKE '%' || @query || '%'
                      )
                )
                SELECT
                    id, source_type, source_entity_id, title, source_path,
                    start_line, end_line, html_url, ordinal, content,
                    character_count, estimated_tokens, indexed_at,
                    COUNT(*) OVER ()::integer
                FROM filtered
                ORDER BY relevance DESC, source_type, title, ordinal
                LIMIT @page_size OFFSET @offset;
                """;
            AddDbParameter(command, "repository_id", id);
            AddDbParameter(command, "query", normalizedQuery);
            AddDbParameter(command, "source_type", normalizedType);
            AddDbParameter(command, "page_size", currentPageSize);
            AddDbParameter(command, "offset", offset);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                matchedChunks = reader.GetInt32(13);
                chunks.Add(new ContentChunkDto(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8),
                    reader.GetString(9),
                    reader.GetInt32(10),
                    reader.GetInt32(11),
                    reader.GetDateTime(12)));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return Results.Ok(new ContentChunkCatalogDto(
            id,
            totalChunks,
            totalSources,
            totalCharacters,
            estimatedTokens,
            matchedChunks,
            currentPage,
            currentPageSize,
            matchedChunks == 0 ? 0 : (int)Math.Ceiling(matchedChunks / (double)currentPageSize),
            facets,
            chunks));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Errore durante la lettura dei chunk del repository {RepositoryId}.", id);
        return Results.Problem(
            title: "Chunk non disponibili",
            detail: "La dashboard non riesce a leggere i chunk con provenienza. Verificare che lo script 007 sia stato applicato.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/api/repositories/{id:long}/commits/{commitId:long}/files", async (
    long id, long commitId, IOneDataGroveDbContext db, CancellationToken ct) =>
{
    var commit = await db.Commits.AsNoTracking()
        .Where(c => c.RepositoryId == id && c.Id == commitId)
        .Select(c => new { c.Sha, RepositoryUrl = c.Repository.HtmlUrl })
        .SingleOrDefaultAsync(ct);
    if (commit is null) return Results.NotFound();
    var files = await db.CommitFiles.AsNoTracking().Where(f => f.CommitId == commitId)
        .OrderBy(f => f.Filename)
        .Select(f => new {
            f.Filename, f.Status, f.Additions, f.Deletions,
            SourceAvailable = db.RepositoryFiles.Any(s => s.RepositoryId == id && s.Path == f.Filename && !s.IsDeleted)
        }).ToListAsync(ct);
    return Results.Ok(files.Select(f => new {
        f.Filename, f.Status, f.Additions, f.Deletions, f.SourceAvailable,
        HtmlUrl = f.Status == "removed"
            ? $"{commit.RepositoryUrl}/commit/{commit.Sha}"
            : $"{commit.RepositoryUrl}/blob/{commit.Sha}/{string.Join('/', f.Filename.Split('/').Select(Uri.EscapeDataString))}"
    }));
});

app.MapGet("/api/repositories/{id:long}/links", async (
    long id,
    string? q,
    string? relation,
    string? evidence,
    string? entityType,
    string? focusType,
    long? focusId,
    string? path,
    bool? review,
    int? page,
    int? pageSize,
    IOneDataGroveDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var normalizedQuery = q?.Trim();
        var normalizedRelation = relation?.Trim().ToLowerInvariant();
        var normalizedEvidence = evidence?.Trim().ToLowerInvariant();
        var normalizedEntityType = entityType?.Trim().ToLowerInvariant();
        var normalizedPage = page is > 0 ? page.GetValueOrDefault() : 1;
        var normalizedPageSize = pageSize is > 0
            ? Math.Min(pageSize.GetValueOrDefault(), 100)
            : 50;

        if (normalizedQuery?.Length > 200)
        {
            return Results.BadRequest(new { message = "La ricerca non può superare 200 caratteri." });
        }

        var allowedRelations = new HashSet<string>(StringComparer.Ordinal)
        {
            "references", "closes", "contains_commit", "modifies_file", "declares_symbol"
        };
        var allowedEvidence = new HashSet<string>(StringComparer.Ordinal)
        {
            "github_api", "text_reference", "structural_index"
        };
        var allowedEntityTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "issue", "issue_comment", "pull_request", "commit", "repository_file", "code_symbol"
        };
        if ((focusType is null) != (focusId is null) ||
            (focusType is not null && (!allowedEntityTypes.Contains(focusType) || focusId <= 0)) ||
            path?.Length > 2048)
        {
            return Results.BadRequest(new { message = "Elemento di navigazione non valido." });
        }
        if (!string.IsNullOrWhiteSpace(normalizedRelation) && !allowedRelations.Contains(normalizedRelation))
        {
            return Results.BadRequest(new { message = "Tipo di relazione non valido." });
        }
        if (!string.IsNullOrWhiteSpace(normalizedEvidence) && !allowedEvidence.Contains(normalizedEvidence))
        {
            return Results.BadRequest(new { message = "Origine del collegamento non valida." });
        }
        if (!string.IsNullOrWhiteSpace(normalizedEntityType) && !allowedEntityTypes.Contains(normalizedEntityType))
        {
            return Results.BadRequest(new { message = "Tipo di elemento non valido." });
        }

        var repositoryExists = await db.Repositories.AsNoTracking()
            .AnyAsync(item => item.Id == id, ct);
        if (!repositoryExists)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = db.Database.GetDbConnection();
            var facets = new List<KnowledgeLinkFacetDto>();
            var totalLinks = 0;
            var reviewLinks = 0;
            DateTime? lastRefreshedAt = null;

            await using (var facetCommand = connection.CreateCommand())
            {
                facetCommand.CommandText = """
                    SELECT
                        link.relation_type,
                        COUNT(*)::integer,
                        MAX(link.refreshed_at),
                        COUNT(*) FILTER
                        (
                            WHERE link.relation_type = 'references'
                              AND link.evidence_type = 'text_reference'
                              AND NOT
                              (
                                  link.source_type = 'commit'
                                  AND source_commit.message ~* '^[[:space:]]*Merge pull request #[1-9][0-9]*'
                              )
                        )::integer AS review_count
                    FROM knowledge.entity_links AS link
                    LEFT JOIN github.commits AS source_commit
                        ON link.source_type = 'commit' AND source_commit.id = link.source_id
                    WHERE link.repository_id = @repository_id
                    GROUP BY link.relation_type
                    ORDER BY COUNT(*) DESC, link.relation_type;
                    """;
                AddDbParameter(facetCommand, "repository_id", id);
                await using var reader = await facetCommand.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var count = reader.GetInt32(1);
                    var refreshedAt = reader.GetDateTime(2);
                    totalLinks += count;
                    reviewLinks += reader.GetInt32(3);
                    if (lastRefreshedAt is null || refreshedAt > lastRefreshedAt)
                    {
                        lastRefreshedAt = refreshedAt;
                    }

                    facets.Add(new KnowledgeLinkFacetDto(reader.GetString(0), count));
                }
            }

            const string linksSql = """
                WITH resolved AS
                (
                SELECT
                    link.id,
                    link.source_type,
                    link.source_id,
                    COALESCE(
                        CASE WHEN source_issue.id IS NOT NULL
                            THEN '#' || source_issue.number || ' · ' || source_issue.title END,
                        CASE WHEN source_comment.id IS NOT NULL
                            THEN 'Commento su #' || source_comment_issue.number END,
                        CASE WHEN source_pull.id IS NOT NULL
                            THEN 'PR #' || source_pull.number || ' · ' || source_pull.title END,
                        CASE WHEN source_commit.id IS NOT NULL
                            THEN LEFT(source_commit.sha, 8) || ' · ' || SPLIT_PART(source_commit.message, E'\n', 1) END,
                        source_file.path,
                        source_symbol.qualified_name,
                        link.source_type || ' ' || link.source_id
                    ) AS source_label,
                    COALESCE(
                        source_issue.html_url,
                        source_comment.html_url,
                        source_pull.html_url,
                        source_commit.html_url,
                        source_file.html_url,
                        CASE WHEN source_symbol_file.html_url IS NOT NULL
                            THEN source_symbol_file.html_url || '#L' || source_symbol.start_line END
                    ) AS source_url,
                    link.target_type,
                    link.target_id,
                    COALESCE(
                        CASE WHEN target_issue.id IS NOT NULL
                            THEN '#' || target_issue.number || ' · ' || target_issue.title END,
                        CASE WHEN target_pull.id IS NOT NULL
                            THEN 'PR #' || target_pull.number || ' · ' || target_pull.title END,
                        CASE WHEN target_commit.id IS NOT NULL
                            THEN LEFT(target_commit.sha, 8) || ' · ' || SPLIT_PART(target_commit.message, E'\n', 1) END,
                        target_file.path,
                        target_symbol.qualified_name,
                        link.target_type || ' ' || link.target_id
                    ) AS target_label,
                    COALESCE(
                        target_issue.html_url,
                        target_pull.html_url,
                        target_commit.html_url,
                        target_file.html_url,
                        CASE WHEN target_symbol_file.html_url IS NOT NULL
                            THEN target_symbol_file.html_url || '#L' || target_symbol.start_line END
                    ) AS target_url,
                    link.relation_type,
                    link.evidence_type,
                    link.evidence_text,
                    link.refreshed_at,
                    COALESCE(source_file.path, source_symbol_file.path) AS source_path,
                    source_symbol.start_line AS source_line,
                    COALESCE(target_file.path, target_symbol_file.path) AS target_path,
                    target_symbol.start_line AS target_line,
                    (
                        link.relation_type = 'references'
                        AND link.evidence_type = 'text_reference'
                        AND NOT
                        (
                            link.source_type = 'commit'
                            AND source_commit.message ~* '^[[:space:]]*Merge pull request #[1-9][0-9]*'
                        )
                    ) AS requires_review
                FROM knowledge.entity_links AS link
                LEFT JOIN github.issues AS source_issue
                    ON link.source_type = 'issue' AND source_issue.id = link.source_id
                LEFT JOIN github.issue_comments AS source_comment
                    ON link.source_type = 'issue_comment' AND source_comment.id = link.source_id
                LEFT JOIN github.issues AS source_comment_issue
                    ON source_comment_issue.id = source_comment.issue_id
                LEFT JOIN github.pull_requests AS source_pull
                    ON link.source_type = 'pull_request' AND source_pull.id = link.source_id
                LEFT JOIN github.commits AS source_commit
                    ON link.source_type = 'commit' AND source_commit.id = link.source_id
                LEFT JOIN github.repository_files AS source_file
                    ON link.source_type = 'repository_file' AND source_file.id = link.source_id
                LEFT JOIN knowledge.code_symbols AS source_symbol
                    ON link.source_type = 'code_symbol' AND source_symbol.id = link.source_id
                LEFT JOIN github.repository_files AS source_symbol_file
                    ON source_symbol_file.id = source_symbol.repository_file_id
                LEFT JOIN github.issues AS target_issue
                    ON link.target_type = 'issue' AND target_issue.id = link.target_id
                LEFT JOIN github.pull_requests AS target_pull
                    ON link.target_type = 'pull_request' AND target_pull.id = link.target_id
                LEFT JOIN github.commits AS target_commit
                    ON link.target_type = 'commit' AND target_commit.id = link.target_id
                LEFT JOIN github.repository_files AS target_file
                    ON link.target_type = 'repository_file' AND target_file.id = link.target_id
                LEFT JOIN knowledge.code_symbols AS target_symbol
                    ON link.target_type = 'code_symbol' AND target_symbol.id = link.target_id
                LEFT JOIN github.repository_files AS target_symbol_file
                    ON target_symbol_file.id = target_symbol.repository_file_id
                WHERE link.repository_id = @repository_id
                  AND (CAST(@focus_id AS bigint) IS NULL
                    OR (link.source_type = @focus_type AND link.source_id = @focus_id)
                    OR (link.target_type = @focus_type AND link.target_id = @focus_id))
                  AND (CAST(@path AS text) IS NULL
                    OR source_file.path = @path OR target_file.path = @path)
                )
                SELECT
                    id, source_type, source_id, source_label, source_url,
                    target_type, target_id, target_label, target_url,
                    relation_type, evidence_type, evidence_text, refreshed_at,
                    COUNT(*) OVER () AS matched_count,
                    source_path, source_line, target_path, target_line,
                    requires_review
                FROM resolved
                WHERE (CAST(@relation AS text) IS NULL OR relation_type = @relation)
                  AND (NOT @review_only OR requires_review)
                  AND (CAST(@evidence AS text) IS NULL OR evidence_type = @evidence)
                  AND
                  (
                      CAST(@entity_type AS text) IS NULL
                      OR source_type = @entity_type
                      OR target_type = @entity_type
                  )
                  AND
                  (
                      CAST(@query AS text) IS NULL
                      OR source_label ILIKE '%' || @query || '%'
                      OR target_label ILIKE '%' || @query || '%'
                      OR COALESCE(evidence_text, '') ILIKE '%' || @query || '%'
                  )
                ORDER BY
                    CASE relation_type
                        WHEN 'closes' THEN 0
                        WHEN 'references' THEN 1
                        WHEN 'contains_commit' THEN 2
                        WHEN 'modifies_file' THEN 3
                        ELSE 4
                    END,
                    refreshed_at DESC,
                    id
                LIMIT @page_size OFFSET @offset;
                """;

            var links = new List<KnowledgeLinkDto>();
            var matchedLinks = 0;
            await using (var linksCommand = connection.CreateCommand())
            {
                linksCommand.CommandText = linksSql;
                AddDbParameter(linksCommand, "focus_type", focusType);
                AddDbParameter(linksCommand, "focus_id", focusId);
                AddDbParameter(linksCommand, "path", path);
                AddDbParameter(linksCommand, "repository_id", id);
                AddDbParameter(linksCommand, "relation", string.IsNullOrWhiteSpace(normalizedRelation) ? null : normalizedRelation);
                AddDbParameter(linksCommand, "review_only", review == true);
                AddDbParameter(linksCommand, "evidence", string.IsNullOrWhiteSpace(normalizedEvidence) ? null : normalizedEvidence);
                AddDbParameter(linksCommand, "entity_type", string.IsNullOrWhiteSpace(normalizedEntityType) ? null : normalizedEntityType);
                AddDbParameter(linksCommand, "query", string.IsNullOrWhiteSpace(normalizedQuery) ? null : normalizedQuery);
                AddDbParameter(linksCommand, "page_size", normalizedPageSize);
                AddDbParameter(linksCommand, "offset", checked((normalizedPage - 1) * normalizedPageSize));
                await using var reader = await linksCommand.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    matchedLinks = checked((int)reader.GetInt64(13));
                    links.Add(new KnowledgeLinkDto(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5),
                        reader.GetInt64(6),
                        reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        reader.GetString(9),
                        reader.GetString(10),
                        reader.IsDBNull(11) ? null : reader.GetString(11),
                        reader.GetDateTime(12),
                        reader.IsDBNull(14) ? null : reader.GetString(14),
                        reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        reader.IsDBNull(16) ? null : reader.GetString(16),
                        reader.IsDBNull(17) ? null : reader.GetInt32(17),
                        reader.GetBoolean(18)));
                }
            }

            return Results.Ok(new KnowledgeLinkCatalogDto(
                id,
                totalLinks,
                matchedLinks,
                normalizedPage,
                normalizedPageSize,
                matchedLinks == 0 ? 0 : (int)Math.Ceiling(matchedLinks / (double)normalizedPageSize),
                lastRefreshedAt,
                reviewLinks,
                facets,
                links));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
    catch (Exception exception)
    {
        app.Logger.LogError(
            exception,
            "Errore durante la lettura dei collegamenti del repository {RepositoryId}.",
            id);
        return Results.Problem(
            title: "Collegamenti non disponibili",
            detail: "La dashboard non riesce a leggere il Knowledge Layer. Verificare lo script 005.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/repositories/{id:long}/verify", async (
    long id,
    IOneDataGroveDbContext db,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    try
    {
        var repository = await db.Repositories.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new
            {
                item.Id,
                item.FullName,
                item.HtmlUrl,
                item.PushedAt,
                item.SyncedAt,
                item.IsSyncEnabled,
                item.IsExcluded
            })
            .SingleOrDefaultAsync(ct);

        if (repository is null)
        {
            return Results.NotFound(new { message = "Repository non trovato." });
        }

        if (repository.IsExcluded)
        {
            return Results.Conflict(new
            {
                message = "Il repository è escluso e i suoi dati sono stati cancellati."
            });
        }

        var findings = new List<VerificationFindingDto>();
        var checks = new List<VerificationCheckDto>();

        var commentsWithGaps = await db.Issues.AsNoTracking()
            .Where(item => item.RepositoryId == id && item.CommentsCount != item.IssueComments.Count)
            .OrderByDescending(item => Math.Abs(item.CommentsCount - item.IssueComments.Count))
            .Take(30)
            .Select(item => new
            {
                item.Number,
                item.Title,
                Expected = item.CommentsCount,
                Imported = item.IssueComments.Count,
                item.HtmlUrl
            })
            .ToListAsync(ct);

        findings.AddRange(commentsWithGaps.Select(item => new VerificationFindingDto(
            item.Expected > item.Imported ? "missing_data" : "unexpected_data",
            item.Expected > item.Imported ? "error" : "warning",
            item.Expected > item.Imported
                ? $"Commenti mancanti nell'issue #{item.Number}"
                : $"Commenti in eccesso nell'issue #{item.Number}",
            $"GitHub indica {item.Expected} commenti, PostgreSQL ne contiene {item.Imported}.",
            "issue", item.Number, item.HtmlUrl)));

        var pullRequestFilesWithGaps = await db.PullRequests.AsNoTracking()
            .Where(item => item.RepositoryId == id && item.ChangedFiles.HasValue &&
                item.ChangedFiles.Value != item.PullRequestFiles.Count)
            .OrderByDescending(item => Math.Abs(item.ChangedFiles!.Value - item.PullRequestFiles.Count))
            .Take(30)
            .Select(item => new
            {
                item.Number,
                item.Title,
                Expected = item.ChangedFiles!.Value,
                Imported = item.PullRequestFiles.Count,
                item.HtmlUrl
            })
            .ToListAsync(ct);

        findings.AddRange(pullRequestFilesWithGaps.Select(item => new VerificationFindingDto(
            item.Expected > item.Imported ? "missing_data" : "unexpected_data",
            item.Expected > item.Imported ? "error" : "warning",
            item.Expected > item.Imported
                ? $"File mancanti nella pull request #{item.Number}"
                : $"File in eccesso nella pull request #{item.Number}",
            $"GitHub indica {item.Expected} file modificati, PostgreSQL ne contiene {item.Imported}.",
            "pull_request", item.Number, item.HtmlUrl)));

        var commitFilesWithGaps = await db.Commits.AsNoTracking()
            .Where(item => item.RepositoryId == id && item.FilesChanged.HasValue &&
                item.FilesChanged.Value != item.CommitFiles.Count)
            .OrderByDescending(item => Math.Abs(item.FilesChanged!.Value - item.CommitFiles.Count))
            .Take(30)
            .Select(item => new
            {
                item.Sha,
                item.Message,
                Expected = item.FilesChanged!.Value,
                Imported = item.CommitFiles.Count,
                item.HtmlUrl
            })
            .ToListAsync(ct);

        findings.AddRange(commitFilesWithGaps.Select(item => new VerificationFindingDto(
            item.Expected > item.Imported ? "missing_data" : "unexpected_data",
            item.Expected > item.Imported ? "error" : "warning",
            item.Expected > item.Imported
                ? $"File mancanti nel commit {item.Sha[..Math.Min(8, item.Sha.Length)]}"
                : $"File in eccesso nel commit {item.Sha[..Math.Min(8, item.Sha.Length)]}",
            $"Il commit indica {item.Expected} file modificati, PostgreSQL ne contiene {item.Imported}.",
            "commit", null, item.HtmlUrl)));

        var internalGapCount = commentsWithGaps.Count + pullRequestFilesWithGaps.Count + commitFilesWithGaps.Count;
        var internalErrorCount = findings.Count(item =>
            item.Category == "missing_data" && item.Severity == "error");
        checks.Add(new VerificationCheckDto(
            "completeness", "Completezza PostgreSQL",
            internalGapCount == 0 ? "healthy" : internalErrorCount > 0 ? "error" : "warning",
            internalGapCount == 0
                ? "Nessuna differenza nei dati collegati rilevata."
                : $"Rilevati {internalGapCount} elementi con conteggi collegati differenti.",
            internalGapCount));

        var syncStates = await db.SyncStates.AsNoTracking()
            .Where(item => item.RepositoryId == id)
            .OrderBy(item => item.ResourceType)
            .Select(item => new
            {
                item.ResourceType,
                item.Status,
                item.LastSuccessfulSync,
                item.LastGithubUpdatedAt,
                item.UpdatedAt
            })
            .ToListAsync(ct);

        if (repository.IsSyncEnabled)
        {
            var staleThreshold = DateTime.UtcNow.AddDays(-7);
            foreach (var syncState in syncStates)
            {
                if (syncState.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new VerificationFindingDto(
                        "stale_sync", "error", $"Sincronizzazione fallita: {syncState.ResourceType}",
                        $"Lo stato è fallito dall'ultimo aggiornamento del {syncState.UpdatedAt:dd/MM/yyyy HH:mm}.",
                        "sync", null, null));
                }
                else if (!syncState.LastSuccessfulSync.HasValue || syncState.LastSuccessfulSync < staleThreshold)
                {
                    findings.Add(new VerificationFindingDto(
                        "stale_sync", "warning", $"Sincronizzazione non recente: {syncState.ResourceType}",
                        syncState.LastSuccessfulSync.HasValue
                            ? $"L'ultima sincronizzazione riuscita risale al {syncState.LastSuccessfulSync:dd/MM/yyyy HH:mm}."
                            : "Non è registrata alcuna sincronizzazione riuscita.",
                        "sync", null, null));
                }
            }

            if (syncStates.Count == 0)
            {
                findings.Add(new VerificationFindingDto(
                    "stale_sync", "error", "Stato di sincronizzazione assente",
                    "Il repository non dispone di risorse monitorate in ingestion.sync_state.",
                    "sync", null, null));
            }
        }

        var staleFindings = findings.Where(item => item.Category == "stale_sync").ToList();
        var staleCount = staleFindings.Count;
        var staleHasErrors = staleFindings.Any(item => item.Severity == "error");
        checks.Add(new VerificationCheckDto(
            "freshness", "Aggiornamento sincronizzazioni",
            staleCount == 0 ? "healthy" : staleHasErrors ? "error" : "warning",
            !repository.IsSyncEnabled
                ? "Sincronizzazione sospesa intenzionalmente dalla dashboard."
                : staleCount == 0
                ? "Le sincronizzazioni risultano recenti e senza errori."
                : $"{staleCount} sincronizzazioni richiedono attenzione.",
            staleCount));

        var githubToken = configuration["GitHub:Token"];
        string? rateLimitRemaining = null;
        var liveDifferenceCount = 0;

        if (string.IsNullOrWhiteSpace(githubToken))
        {
            findings.Add(new VerificationFindingDto(
                "github_comparison", "warning", "Confronto GitHub non disponibile",
                "Il token GitHub non è configurato per il servizio della dashboard.",
                "github", null, repository.HtmlUrl));
            checks.Add(new VerificationCheckDto(
                "github", "Confronto con GitHub", "warning",
                "Verifica locale completata; confronto live non eseguito.", 1));
        }
        else
        {
            try
            {
                var github = httpClientFactory.CreateClient();
                github.BaseAddress = new Uri("https://api.github.com/");
                github.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", githubToken);
                github.DefaultRequestHeaders.UserAgent.ParseAdd("iOneDataGrove-Dashboard/1.0");
                github.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                github.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

                var databaseIssues = await db.Issues.AsNoTracking()
                    .Where(item => item.RepositoryId == id)
                    .Select(item => new { item.Number, item.State, item.HtmlUrl })
                    .ToDictionaryAsync(item => item.Number, ct);
                var databasePullRequests = await db.PullRequests.AsNoTracking()
                    .Where(item => item.RepositoryId == id)
                    .Select(item => new { item.Number, item.State, item.Merged, item.HtmlUrl })
                    .ToDictionaryAsync(item => item.Number, ct);
                var githubIssueNumbers = new HashSet<int>();
                var githubPullRequestNumbers = new HashSet<int>();
                var completeGitHubCatalog = true;
                var liveFindingsStart = findings.Count;
                const int maxGitHubPages = 10;

                for (var page = 1; page <= maxGitHubPages; page++)
                {
                    using var issuesResponse = await github.GetAsync(
                        $"repos/{repository.FullName}/issues?state=all&sort=updated&direction=desc&per_page=100&page={page}",
                        ct);

                    rateLimitRemaining = issuesResponse.Headers.TryGetValues(
                        "X-RateLimit-Remaining",
                        out var rateValues)
                        ? rateValues.FirstOrDefault()
                        : rateLimitRemaining;

                    issuesResponse.EnsureSuccessStatusCode();

                    var githubIssuesJson = await issuesResponse.Content.ReadAsStringAsync(ct);
                    using var githubIssues = System.Text.Json.JsonDocument.Parse(githubIssuesJson);
                    var pageItemCount = githubIssues.RootElement.GetArrayLength();

                    foreach (var githubItem in githubIssues.RootElement.EnumerateArray())
                    {
                        var number = githubItem.GetProperty("number").GetInt32();
                        var title = githubItem.GetProperty("title").GetString() ?? "Senza titolo";
                        var stateValue = githubItem.GetProperty("state").GetString() ?? "unknown";
                        var htmlUrl = githubItem.GetProperty("html_url").GetString();
                        var isPullRequest = githubItem.TryGetProperty("pull_request", out var pullRequestInfo);

                        if (isPullRequest)
                        {
                            githubPullRequestNumbers.Add(number);
                            if (!databasePullRequests.TryGetValue(number, out var databaseItem))
                            {
                                findings.Add(new VerificationFindingDto(
                                    "missing_data", "error", $"Pull request GitHub non importata: #{number}",
                                    title, "pull_request", number, htmlUrl));
                                liveDifferenceCount++;
                                continue;
                            }

                            var isMergedOnGithub = pullRequestInfo.TryGetProperty("merged_at", out var mergedAt) &&
                                mergedAt.ValueKind != System.Text.Json.JsonValueKind.Null;
                            if (!databaseItem.State.Equals(stateValue, StringComparison.OrdinalIgnoreCase) ||
                                databaseItem.Merged != isMergedOnGithub)
                            {
                                findings.Add(new VerificationFindingDto(
                                    "state_difference", "warning", $"Stato differente nella pull request #{number}",
                                    $"GitHub: {stateValue}{(isMergedOnGithub ? ", unita" : "")}; PostgreSQL: {databaseItem.State}{(databaseItem.Merged ? ", unita" : "")}.",
                                    "pull_request", number, htmlUrl));
                                liveDifferenceCount++;
                            }
                        }
                        else
                        {
                            githubIssueNumbers.Add(number);
                            if (!databaseIssues.TryGetValue(number, out var databaseItem))
                            {
                                findings.Add(new VerificationFindingDto(
                                    "missing_data", "error", $"Issue GitHub non importata: #{number}",
                                    title, "issue", number, htmlUrl));
                                liveDifferenceCount++;
                                continue;
                            }

                            if (!databaseItem.State.Equals(stateValue, StringComparison.OrdinalIgnoreCase))
                            {
                                findings.Add(new VerificationFindingDto(
                                    "state_difference", "warning", $"Stato differente nell'issue #{number}",
                                    $"GitHub: {stateValue}; PostgreSQL: {databaseItem.State}.",
                                    "issue", number, htmlUrl));
                                liveDifferenceCount++;
                            }
                        }
                    }

                    if (pageItemCount < 100)
                    {
                        break;
                    }

                    if (page == maxGitHubPages)
                    {
                        completeGitHubCatalog = false;
                    }
                }

                if (completeGitHubCatalog)
                {
                    foreach (var databaseItem in databaseIssues.Values
                                 .Where(item => !githubIssueNumbers.Contains(item.Number)))
                    {
                        findings.Add(new VerificationFindingDto(
                            "unexpected_data", "warning",
                            $"Issue PostgreSQL non presente su GitHub: #{databaseItem.Number}",
                            "Il record potrebbe essere obsoleto e richiede una nuova importazione completa.",
                            "issue", databaseItem.Number, databaseItem.HtmlUrl));
                        liveDifferenceCount++;
                    }

                    foreach (var databaseItem in databasePullRequests.Values
                                 .Where(item => !githubPullRequestNumbers.Contains(item.Number)))
                    {
                        findings.Add(new VerificationFindingDto(
                            "unexpected_data", "warning",
                            $"Pull request PostgreSQL non presente su GitHub: #{databaseItem.Number}",
                            "Il record potrebbe essere obsoleto e richiede una nuova importazione completa.",
                            "pull_request", databaseItem.Number, databaseItem.HtmlUrl));
                        liveDifferenceCount++;
                    }
                }
                else
                {
                    findings.Add(new VerificationFindingDto(
                        "github_comparison", "warning", "Confronto GitHub limitato",
                        "Il repository supera 1.000 issue e pull request; il controllo non può individuare eventuali record PostgreSQL in eccesso oltre questo limite.",
                        "github", null, repository.HtmlUrl));
                }

                var liveHasErrors = findings.Skip(liveFindingsStart)
                    .Any(item => item.Severity == "error");
                var githubCheckStatus = liveHasErrors
                    ? "error"
                    : liveDifferenceCount > 0 || !completeGitHubCatalog ? "warning" : "healthy";
                checks.Add(new VerificationCheckDto(
                    "github", "Confronto con GitHub", githubCheckStatus,
                    liveDifferenceCount == 0 && completeGitHubCatalog
                        ? $"Tutti i {githubIssueNumbers.Count + githubPullRequestNumbers.Count} elementi GitHub coincidono con PostgreSQL."
                        : $"Rilevate {liveDifferenceCount} differenze nel catalogo controllato.",
                    liveDifferenceCount));
            }
            catch (Exception githubException)
            {
                app.Logger.LogWarning(githubException, "Confronto GitHub non riuscito per {RepositoryId}.", id);
                findings.Add(new VerificationFindingDto(
                    "github_comparison", "warning", "GitHub non raggiungibile durante la verifica",
                    "I controlli PostgreSQL sono completi, ma il confronto live non è riuscito. Riprova più tardi.",
                    "github", null, repository.HtmlUrl));
                checks.Add(new VerificationCheckDto(
                    "github", "Confronto con GitHub", "warning",
                    "Confronto live non completato.", 1));
            }
        }

        var orderedFindings = findings
            .OrderBy(item => item.Severity == "error" ? 0 : 1)
            .ThenBy(item => item.Category)
            .Take(100)
            .ToList();
        var errorCount = findings.Count(item => item.Severity == "error");
        var warningCount = findings.Count(item => item.Severity == "warning");
        var status = errorCount > 0 ? "error" : warningCount > 0 ? "attention" : "healthy";
        var summary = errorCount > 0
            ? $"{errorCount} problemi e {warningCount} avvisi rilevati."
            : warningCount > 0
                ? $"Nessun dato mancante grave; {warningCount} avvisi da controllare."
                : "Nessuna anomalia rilevata nei dati controllati.";

        return Results.Ok(new RepositoryVerificationDto(
            DateTime.UtcNow,
            repository.Id,
            repository.FullName,
            status,
            summary,
            errorCount,
            warningCount,
            rateLimitRemaining,
            checks,
            orderedFindings));
    }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Errore durante la verifica del repository {RepositoryId}.", id);
        return Results.Problem(
            title: "Verifica non disponibile",
            detail: "La dashboard non riesce a completare i controlli sul repository.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

static void AddDbParameter(System.Data.Common.DbCommand command, string name, object? value)
{
    var parameter = command.CreateParameter();
    parameter.ParameterName = name;
    parameter.Value = value ?? DBNull.Value;
    command.Parameters.Add(parameter);
}

static async Task<int> CountContentChunksAsync(
    IOneDataGroveDbContext db,
    CancellationToken cancellationToken)
{
    await db.Database.OpenConnectionAsync(cancellationToken);
    try
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*)::integer FROM knowledge.content_chunks;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
}
static async Task<int> CountStructuralRowsAsync(
    IOneDataGroveDbContext db,
    long repositoryId,
    CancellationToken cancellationToken)
{
    await using var command = db.Database.GetDbConnection().CreateCommand();
    command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
    command.CommandText = """
        SELECT
            (SELECT COUNT(*) FROM knowledge.code_file_indexes WHERE repository_id = @repository_id) +
            (SELECT COUNT(*) FROM knowledge.code_symbols WHERE repository_id = @repository_id);
        """;
    AddDbParameter(command, "repository_id", repositoryId);
    var result = await command.ExecuteScalarAsync(cancellationToken);
    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
}

static bool IsDashboardManagementRequest(HttpRequest request) =>
    request.Headers.TryGetValue("X-iOneDataGrove-Management", out var values) &&
    values.Count == 1 &&
    string.Equals(values[0], "dashboard-local", StringComparison.Ordinal);

static IResult ImportInProgressResult() => Results.Conflict(new
{
    message = "Importazione in corso. Attendi che termini prima di modificare o cancellare un repository."
});

app.Run();

internal sealed record ContentChunkCatalogDto(
    long RepositoryId,
    int TotalChunks,
    int TotalSources,
    long TotalCharacters,
    long EstimatedTokens,
    int MatchedChunks,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<ContentChunkFacetDto> Facets,
    IReadOnlyList<ContentChunkDto> Chunks);

internal sealed record ContentChunkFacetDto(string SourceType, int Count);

internal sealed record ContentChunkDto(
    long Id,
    string SourceType,
    long SourceEntityId,
    string Title,
    string? SourcePath,
    int? StartLine,
    int? EndLine,
    string? HtmlUrl,
    int Ordinal,
    string Content,
    int CharacterCount,
    int EstimatedTokens,
    DateTime IndexedAt);
internal sealed record GlobalSearchCatalogDto(
    string Query,
    long? RepositoryId,
    string? EntityType,
    int MatchedResults,
    int Page,
    int PageSize,
    int TotalPages,
    IReadOnlyList<GlobalSearchResultDto> Results);

internal sealed record GlobalSearchResultDto(
    string EntityType,
    long EntityId,
    long RepositoryId,
    string RepositoryFullName,
    string Title,
    string Snippet,
    string? HtmlUrl,
    string? SourcePath,
    int? SourceLine,
    DateTime UpdatedAt,
    double Relevance);
internal sealed record DashboardDto(
    DateTime GeneratedAt,
    string Status,
    string TrackingStatus,
    DateTime? LatestDataSync,
    TotalsDto Totals,
    SyncOverviewDto SyncOverview,
    KnowledgeQualitySummaryDto KnowledgeQuality,
    IReadOnlyList<RepositoryDto> Repositories,
    IReadOnlyList<SyncStateDto> SyncStates,
    IReadOnlyList<SyncRunDto> RecentRuns);

internal sealed record TotalsDto(
    int Records,
    int Repositories,
    int ActiveRepositories,
    int PausedRepositories,
    int ExcludedRepositories,
    int Users,
    int Issues,
    int OpenIssues,
    int Comments,
    int PullRequests,
    int OpenPullRequests,
    int MergedPullRequests,
    int Commits,
    int ChangedFileRecords,
    int SourceFiles,
    int ContentChunks);

internal sealed record SyncOverviewDto(
    int TrackedResources,
    int CompletedResources,
    int FailedResources,
    int RunningResources,
    int PendingResources,
    int StaleResources,
    int UntrackedRepositories,
    int TotalRuns,
    int FailedRunsInHistory,
    DateTime? LatestRunAt);

internal sealed record KnowledgeQualitySummaryDto(
    int ReviewLinks,
    int RepositoriesWithReviewLinks,
    IReadOnlyList<KnowledgeReviewRepositoryDto> Repositories);

internal sealed record KnowledgeReviewRepositoryDto(
    long RepositoryId,
    string RepositoryFullName,
    int ReviewLinks);

internal sealed record RepositoryDto(
    long Id,
    string FullName,
    string? Description,
    string HtmlUrl,
    bool IsPrivate,
    bool IsArchived,
    bool IsSyncEnabled,
    DateTime? SyncDisabledAt,
    bool IsExcluded,
    DateTime? ExcludedAt,
    string? DefaultBranch,
    string? PrimaryLanguage,
    DateTime? PushedAt,
    DateTime SyncedAt,
    int Issues,
    int OpenIssues,
    int PullRequests,
    int OpenPullRequests,
    int MergedPullRequests,
    int Commits);

internal sealed record SyncStateDto(
    long RepositoryId,
    string RepositoryFullName,
    string ResourceType,
    string Status,
    DateTime? LastSuccessfulSync,
    DateTime? LastGithubUpdatedAt,
    DateTime UpdatedAt);

internal sealed record SyncRunDto(
    long Id,
    long? RepositoryId,
    string? RepositoryFullName,
    string? ResourceType,
    string SyncType,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int ItemsRead,
    int ItemsInserted,
    int ItemsUpdated,
    int ItemsFailed,
    string? ErrorMessage);

internal sealed record RepositoryExplorerDto(
    DateTime GeneratedAt,
    RepositoryDetailDto Repository,
    IReadOnlyList<RepositoryOptionDto> RepositoryOptions,
    IReadOnlyList<RepositorySyncStateDto> SyncStates,
    IReadOnlyList<IssueExplorerDto> Issues,
    IReadOnlyList<PullRequestExplorerDto> PullRequests,
    IReadOnlyList<CommitExplorerDto> Commits,
    IReadOnlyList<FileExplorerDto> Files,
    ExplorerResultCountsDto ResultCounts);

internal sealed record RepositoryDetailDto(
    long Id,
    string FullName,
    string? Description,
    string HtmlUrl,
    bool IsPrivate,
    bool IsArchived,
    bool IsSyncEnabled,
    DateTime? SyncDisabledAt,
    bool IsExcluded,
    DateTime? ExcludedAt,
    string? DefaultBranch,
    string? PrimaryLanguage,
    DateTime? CreatedAt,
    DateTime? PushedAt,
    DateTime SyncedAt,
    int Issues,
    int OpenIssues,
    int PullRequests,
    int OpenPullRequests,
    int MergedPullRequests,
    int Commits,
    int Files,
    int SourceFiles);

internal sealed record RepositoryOptionDto(long Id, string FullName);

internal sealed record RepositorySynchronizationRequest(bool Enabled);

internal sealed record RepositoryDeleteRequest(string? RepositoryFullName);

internal sealed record RepositoryControlDto(
    long Id,
    string FullName,
    bool IsSyncEnabled,
    DateTime? SyncDisabledAt,
    bool IsExcluded,
    DateTime? ExcludedAt);

internal sealed record RepositoryDeletionResultDto(
    long Id,
    string FullName,
    DateTime ExcludedAt,
    int DeletedRecords);

internal sealed record RepositorySyncStateDto(
    string ResourceType,
    string Status,
    DateTime? LastSuccessfulSync,
    DateTime? LastGithubUpdatedAt,
    DateTime UpdatedAt);

internal sealed record IssueExplorerDto(
    long Id,
    int Number,
    string Title,
    string State,
    string? Author,
    int Comments,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    string? HtmlUrl);

internal sealed record PullRequestExplorerDto(
    long Id,
    int Number,
    string Title,
    string State,
    bool Merged,
    bool IsDraft,
    string? Author,
    string? BaseBranch,
    string? HeadBranch,
    int? Commits,
    int? ChangedFiles,
    int? Additions,
    int? Deletions,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? MergedAt,
    string? HtmlUrl);

internal sealed record CommitExplorerDto(
    long Id,
    string Sha,
    string Message,
    string? Author,
    DateTime? AuthoredAt,
    DateTime? CommittedAt,
    int? Additions,
    int? Deletions,
    int? FilesChanged,
    string? HtmlUrl);

internal sealed record FileExplorerDto(
    string Filename,
    int Touches,
    int Additions,
    int Deletions,
    int Changes,
    DateTime? LastTouchedAt);

internal sealed record ExplorerResultCountsDto(
    int Issues,
    int PullRequests,
    int Commits,
    int Files,
    int SourceFiles);

internal sealed record SourceCatalogDto(
    long RepositoryId,
    string RepositoryFullName,
    string? DefaultBranch,
    int FileCount,
    int DirectoryCount,
    long TotalLines,
    long TotalSizeBytes,
    DateTime? LastSyncedAt,
    IReadOnlyList<SourceFacetDto> Languages,
    IReadOnlyList<SourceFacetDto> Extensions,
    IReadOnlyList<SourceFileSummaryDto> Files);

internal sealed record SourceFacetDto(string Name, int Count);

internal sealed record SourceFileSummaryDto(
    string Path,
    string FileName,
    string? Extension,
    string? Language,
    string Branch,
    string BlobSha,
    long SizeBytes,
    int LineCount,
    DateTime SyncedAt);

internal sealed record SourceFileContentDto(
    string Path,
    string FileName,
    string? Extension,
    string? Language,
    string Branch,
    string BlobSha,
    long SizeBytes,
    int LineCount,
    string ContentEncoding,
    string Content,
    string HtmlUrl,
    DateTime SyncedAt);

internal sealed record SourceSearchResultDto(
    string Query,
    string? Extension,
    int MatchedFiles,
    bool IsLimited,
    string SearchMode,
    IReadOnlyList<SourceSearchMatchDto> Matches);

internal sealed record SourceSearchMatchDto(
    string Path,
    string? Language,
    string? Extension,
    int? LineNumber,
    string MatchType,
    string Snippet,
    float Relevance);

internal sealed record CSharpSymbolCatalogDto(
    long RepositoryId,
    int CSharpFiles,
    int IndexedFiles,
    int PartialFiles,
    int SymbolCount,
    int MatchedSymbols,
    bool IsLimited,
    DateTime? LastIndexedAt,
    string? Query,
    string? Kind,
    IReadOnlyList<SourceFacetDto> Kinds,
    IReadOnlyList<CSharpSymbolDto> Symbols);

internal sealed record CSharpSymbolDto(
    string Kind,
    string Name,
    string QualifiedName,
    string Signature,
    string? ContainingSymbol,
    string? Accessibility,
    IReadOnlyList<string> Modifiers,
    string? ReturnType,
    int StartLine,
    int EndLine,
    string Path);

internal sealed record KnowledgeLinkCatalogDto(
    long RepositoryId,
    int TotalLinks,
    int MatchedLinks,
    int Page,
    int PageSize,
    int TotalPages,
    DateTime? LastRefreshedAt,
    int ReviewLinks,
    IReadOnlyList<KnowledgeLinkFacetDto> Facets,
    IReadOnlyList<KnowledgeLinkDto> Links);

internal sealed record KnowledgeLinkFacetDto(string RelationType, int Count);

internal sealed record KnowledgeLinkDto(
    long Id,
    string SourceType,
    long SourceId,
    string SourceLabel,
    string? SourceUrl,
    string TargetType,
    long TargetId,
    string TargetLabel,
    string? TargetUrl,
    string RelationType,
    string EvidenceType,
    string? EvidenceText,
    DateTime RefreshedAt,
    string? SourcePath,
    int? SourceLine,
    string? TargetPath,
    int? TargetLine,
    bool RequiresReview);

internal sealed record RepositoryVerificationDto(
    DateTime CheckedAt,
    long RepositoryId,
    string RepositoryFullName,
    string Status,
    string Summary,
    int ErrorCount,
    int WarningCount,
    string? GitHubRateLimitRemaining,
    IReadOnlyList<VerificationCheckDto> Checks,
    IReadOnlyList<VerificationFindingDto> Findings);

internal sealed record VerificationCheckDto(
    string Key,
    string Label,
    string Status,
    string Summary,
    int Count);

internal sealed record VerificationFindingDto(
    string Category,
    string Severity,
    string Title,
    string Detail,
    string EntityType,
    int? EntityNumber,
    string? HtmlUrl);

internal sealed class DashboardImportExecutionLock : IAsyncDisposable
{
    private readonly IOneDataGroveDbContext dbContext;
    private bool acquired;

    private DashboardImportExecutionLock(IOneDataGroveDbContext dbContext)
    {
        this.dbContext = dbContext;
        acquired = true;
    }

    public static async Task<DashboardImportExecutionLock?> TryAcquireAsync(
        IOneDataGroveDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
            AddLockParameter(command);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is true)
            {
                return new DashboardImportExecutionLock(dbContext);
            }

            await dbContext.Database.CloseConnectionAsync();
            return null;
        }
        catch
        {
            await dbContext.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!acquired)
        {
            return;
        }

        acquired = false;
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
            AddLockParameter(command);
            await command.ExecuteScalarAsync();
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static void AddLockParameter(System.Data.Common.DbCommand command)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = ImportAdvisoryLock.ApplicationLockKey;
        command.Parameters.Add(parameter);
    }
}
