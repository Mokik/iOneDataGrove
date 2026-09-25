using System.Text.Json;
using iOneDataGrove.Persistence.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace iOneDataGrove.Importer;

internal static class ImporterApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration.AddUserSecrets<Program>(optional: true);
        builder.Logging.AddFilter(
            "Microsoft.EntityFrameworkCore.Database.Command",
            LogLevel.Warning);

        var connectionString = builder.Configuration.GetConnectionString("iOneDataGrove");
        var githubToken = builder.Configuration["GitHub:Token"];
        var chunksOnly = builder.Configuration.GetValue<bool>("Import:ChunksOnly") ||
            args.Any(argument => string.Equals(argument, "--chunks-only", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'iOneDataGrove' non configurata. " +
                "Usa dotnet user-secrets nel progetto iOneDataGrove.Importer.");
        }

        if (string.IsNullOrWhiteSpace(githubToken))
        {
            throw new InvalidOperationException(
                "Token GitHub non configurato. Usa il secret 'GitHub:Token'.");
        }

        builder.Services.AddDbContext<IOneDataGroveDbContext>(options =>
            options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention());

        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<IOneDataGroveDbContext>();
        var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);

        Console.WriteLine($"Connessione iOneDataGrove: {canConnect}");

        if (!canConnect)
        {
            return 1;
        }

        await using var executionLock = await ImportExecutionLock.TryAcquireAsync(
            dbContext,
            cancellationToken);
        if (executionLock is null)
        {
            Console.Error.WriteLine(
                "Un'altra importazione è già in esecuzione. Attendi che termini prima di riprovare.");
            return 2;
        }

        var recoveryRepository = builder.Configuration["RecoverCommit:Repository"];
        var recoverySha = builder.Configuration["RecoverCommit:Sha"];
        if (recoveryRepository is not null || recoverySha is not null)
        {
            if (string.IsNullOrWhiteSpace(recoveryRepository) || string.IsNullOrWhiteSpace(recoverySha))
                throw new ArgumentException("RecoverCommit richiede Repository e Sha.");
            var commitId = await new GitHubCommitRecovery(dbContext, githubToken)
                .ImportAsync(recoveryRepository, recoverySha, cancellationToken);
            var repositoryId = await dbContext.Repositories.Where(r => r.FullName == recoveryRepository)
                .Select(r => r.Id).SingleAsync(cancellationToken);
            var addedLinks = await new KnowledgeLinkIndexer(dbContext).IndexCommitAsync(
                repositoryId, recoveryRepository, commitId, cancellationToken);
            Console.WriteLine($"Recupero commit {recoverySha}: id={commitId}, repository={recoveryRepository}, nuovi collegamenti={addedLinks}.");
            return 0;
        }

        var recoveredRuns = await new IngestionSyncTracker(dbContext)
            .RecoverInterruptedRunsAsync(cancellationToken);
        if (recoveredRuns > 0)
        {
            Console.WriteLine(
                $"Recuperate {recoveredRuns} esecuzioni rimaste aperte da un arresto precedente.");
        }

        var forceFull = args.Any(argument =>
            string.Equals(argument, "--full", StringComparison.OrdinalIgnoreCase));
        var selectedRepository = builder.Configuration["Import:Repository"] ??
            ReadArgumentValue(args, "--Import:Repository");
        if (chunksOnly)
        {
            return await IndexExistingChunksAsync(
                dbContext, selectedRepository, forceFull, cancellationToken);
        }

        var commitsOnly = builder.Configuration.GetValue<bool>("Import:CommitsOnly");
        if (commitsOnly)
        {
            if (string.IsNullOrWhiteSpace(selectedRepository))
                throw new ArgumentException("Import:CommitsOnly richiede Import:Repository.");
            var selectedId = await dbContext.Repositories.Where(r => r.FullName == selectedRepository)
                .Select(r => r.Id).SingleAsync(cancellationToken);
            await new GitHubBranchCommitImporter(dbContext, githubToken)
                .ImportAsync(selectedId, forceFull, cancellationToken);
            return 0;
        }
        var defaultSourceOptions = SourceImportOptions.Default;
        var sourceOptions = new SourceImportOptions(
            builder.Configuration.GetValue(
                "SourceImport:MaxFileSizeBytes",
                defaultSourceOptions.MaxFileSizeBytes),
            builder.Configuration.GetValue(
                "SourceImport:ArchiveThreshold",
                defaultSourceOptions.ArchiveThreshold),
            builder.Configuration.GetValue(
                "SourceImport:MaxArchiveSizeBytes",
                defaultSourceOptions.MaxArchiveSizeBytes));
        ValidateSourceOptions(sourceOptions);
        var discovery = new GitHubRepositoryDiscovery(githubToken);
        var repositories = await discovery.GetAllAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(selectedRepository))
        {
            repositories = repositories.Where(r => string.Equals(r.FullName, selectedRepository, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (repositories.Count == 0) throw new ArgumentException("Repository richiesto non accessibile al token.");
        }

        Console.WriteLine(
            $"Repository accessibili al token: {repositories.Count}. " +
            $"Modalità: {(forceFull ? "full" : "incrementale")}.");

        if (repositories.Count == 0)
        {
            Console.WriteLine("Nessun repository accessibile: importazione terminata.");
            return 0;
        }

        var failures = new List<string>();
        var importedRepositories = 0;
        var partiallyImportedRepositories = 0;
        var skippedRepositories = 0;

        for (var index = 0; index < repositories.Count; index++)
        {
            var repository = repositories[index];
            Console.WriteLine();
            Console.WriteLine(
                $"[{index + 1}/{repositories.Count}] Sincronizzazione {repository.FullName}");

            var importControl = await dbContext.Repositories.AsNoTracking()
                .Where(item => item.GithubId == repository.GithubId)
                .Select(item => new
                {
                    item.IsSyncEnabled,
                    item.IsExcluded
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (importControl is { IsExcluded: true })
            {
                skippedRepositories++;
                Console.WriteLine(
                    "Repository escluso dalla dashboard: nessun dato verrà acquisito.");
                continue;
            }

            if (importControl is { IsSyncEnabled: false })
            {
                skippedRepositories++;
                Console.WriteLine(
                    "Sincronizzazione sospesa dalla dashboard: repository ignorato.");
                continue;
            }

            try
            {
                var failureCountBeforeRepository = failures.Count;
                await ImportRepositoryAsync(
                    dbContext,
                    githubToken,
                    repository,
                    forceFull,
                    sourceOptions,
                    failures,
                    cancellationToken);

                if (failures.Count == failureCountBeforeRepository)
                {
                    importedRepositories++;
                }
                else
                {
                    partiallyImportedRepositories++;
                }

                dbContext.ChangeTracker.Clear();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                dbContext.ChangeTracker.Clear();
                throw;
            }
            catch (Exception exception)
            {
                dbContext.ChangeTracker.Clear();
                failures.Add($"{repository.FullName}: repository - {IngestionSyncTracker.FormatException(exception)}");
                Console.Error.WriteLine(
                    $"Repository {repository.FullName} non sincronizzato: {IngestionSyncTracker.FormatException(exception)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Importazione terminata: accessibili={repositories.Count}, " +
            $"sincronizzati={importedRepositories}, parziali={partiallyImportedRepositories}, " +
            $"esclusi={skippedRepositories}, " +
            $"errori={failures.Count}.");

        if (failures.Count == 0)
        {
            return 0;
        }

        Console.Error.WriteLine("Dettaglio errori:");
        foreach (var failure in failures)
        {
            Console.Error.WriteLine($"- {failure}");
        }

        return 1;
    }

    private static async Task<int> IndexExistingChunksAsync(
        IOneDataGroveDbContext dbContext,
        string? selectedRepository,
        bool forceFull,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Repositories.AsNoTracking()
            .Where(repository => repository.IsSyncEnabled && !repository.IsExcluded);
        if (!string.IsNullOrWhiteSpace(selectedRepository))
        {
            query = query.Where(repository => repository.FullName == selectedRepository);
        }

        var repositories = await query
            .OrderBy(repository => repository.FullName)
            .Select(repository => new { repository.Id, repository.FullName })
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(selectedRepository) && repositories.Count == 0)
        {
            throw new ArgumentException(
                "Il repository richiesto non è presente, è sospeso oppure è escluso dal database locale.");
        }

        Console.WriteLine(
            $"Generazione chunk locale: repository={repositories.Count}, " +
            $"modalità={(forceFull ? "full" : "incrementale")}. Nessuna chiamata GitHub.");
        var failures = new List<string>();
        for (var index = 0; index < repositories.Count; index++)
        {
            var repository = repositories[index];
            Console.WriteLine();
            Console.WriteLine(
                $"[{index + 1}/{repositories.Count}] Chunk {repository.FullName}");
            await IndexContentChunksAsync(
                dbContext,
                repository.FullName,
                repository.Id,
                forceFull,
                failures,
                cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Generazione chunk terminata: repository={repositories.Count}, " +
            $"errori={failures.Count}.");
        if (failures.Count == 0) return 0;

        Console.Error.WriteLine("Dettaglio errori:");
        foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
        return 1;
    }

    private static async Task ImportRepositoryAsync(
        IOneDataGroveDbContext dbContext,
        string githubToken,
        GitHubRepositoryReference repository,
        bool forceFull,
        SourceImportOptions sourceOptions,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var repositoryImporter = new GitHubRepositoryImporter(dbContext, githubToken);
        var result = await repositoryImporter.ImportAsync(
            repository.Owner,
            repository.Name,
            cancellationToken);

        Console.WriteLine(
            $"Repository sincronizzato: {result.FullName} " +
            $"(id interno: {result.RepositoryId}, " +
            $"owner inserito: {result.OwnerInserted}, " +
            $"repository inserito: {result.RepositoryInserted})");

        await ImportIssuesAsync(
            dbContext,
            githubToken,
            repository,
            result.RepositoryId,
            forceFull,
            failures,
            cancellationToken);
        await ImportPullRequestsAsync(
            dbContext,
            githubToken,
            repository,
            result.RepositoryId,
            forceFull,
            failures,
            cancellationToken);
        try
        {
            await new GitHubBranchCommitImporter(dbContext, githubToken)
                .ImportAsync(result.RepositoryId, forceFull, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            failures.Add($"{repository.FullName}: commits - {IngestionSyncTracker.FormatException(exception)}");
            Console.Error.WriteLine($"Commit di {repository.FullName} non sincronizzati: {IngestionSyncTracker.FormatException(exception)}");
        }
        var sourceImportCompleted = await ImportSourceFilesAsync(
            dbContext,
            githubToken,
            repository,
            result.RepositoryId,
            forceFull,
            sourceOptions,
            failures,
            cancellationToken);

        if (sourceImportCompleted)
        {
            await IndexCSharpSymbolsAsync(
                dbContext,
                repository,
                result.RepositoryId,
                forceFull,
                failures,
                cancellationToken);
        }
        else
        {
            Console.Error.WriteLine(
                $"Struttura C# di {repository.FullName} non eseguita: " +
                "la sincronizzazione del codice sorgente non è riuscita.");
        }

        await IndexKnowledgeLinksAsync(
            dbContext,
            repository,
            result.RepositoryId,
            forceFull,
            failures,
            cancellationToken);

        if (sourceImportCompleted)
        {
            await IndexContentChunksAsync(
                dbContext,
                repository.FullName,
                result.RepositoryId,
                forceFull,
                failures,
                cancellationToken);
        }
        else
        {
            Console.Error.WriteLine(
                $"Chunk di {repository.FullName} non eseguiti: " +
                "la sincronizzazione del codice sorgente non è riuscita.");
        }
    }

    private static async Task IndexContentChunksAsync(
        IOneDataGroveDbContext dbContext,
        string repositoryFullName,
        long repositoryId,
        bool forceFull,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var syncTracker = new IngestionSyncTracker(dbContext);
        SyncExecution? execution = null;

        try
        {
            execution = await syncTracker.StartAsync(
                repositoryId,
                "content_chunks",
                forceFull,
                cancellationToken);
            var result = await new ContentChunkIndexer(dbContext).IndexAsync(
                repositoryId,
                forceFull,
                cancellationToken);

            await syncTracker.CompleteAsync(
                execution,
                new SyncMetrics(
                    result.Sources,
                    result.WrittenChunks,
                    result.IndexedSources + result.RemovedSources,
                    JsonSerializer.Serialize(new
                    {
                        result.IndexedSources,
                        result.UnchangedSources,
                        result.RemovedSources,
                        result.TotalChunks,
                        ChunkerVersion = ContentChunkIndexer.ChunkerVersion
                    })),
                cancellationToken);

            Console.WriteLine(
                $"Chunk con provenienza: fonti={result.Sources}, " +
                $"aggiornate={result.IndexedSources}, invariate={result.UnchangedSources}, " +
                $"rimosse={result.RemovedSources}, scritti={result.WrittenChunks}, " +
                $"totali={result.TotalChunks}.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            failures.Add(
                $"{repositoryFullName}: content_chunks - " +
                IngestionSyncTracker.FormatException(exception));
            Console.Error.WriteLine(
                $"Chunk di {repositoryFullName} non indicizzati: " +
                $"{IngestionSyncTracker.FormatException(exception)}. " +
                "Verificare che lo script 007 sia stato applicato.");
        }
    }

    private static async Task IndexKnowledgeLinksAsync(
        IOneDataGroveDbContext dbContext,
        GitHubRepositoryReference repository,
        long repositoryId,
        bool forceFull,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var syncTracker = new IngestionSyncTracker(dbContext);
        SyncExecution? execution = null;

        try
        {
            execution = await syncTracker.StartAsync(
                repositoryId,
                "knowledge_links",
                forceFull,
                cancellationToken);
            var indexer = new KnowledgeLinkIndexer(dbContext);
            var result = await indexer.IndexAsync(
                repositoryId,
                repository.FullName,
                cancellationToken);

            await syncTracker.CompleteAsync(
                execution,
                new SyncMetrics(
                    result.Total,
                    result.Total,
                    0,
                    JsonSerializer.Serialize(new
                    {
                        result.PullRequestCommits,
                        result.PullRequestFiles,
                        result.CommitFiles,
                        result.DeclaredSymbols,
                        result.TextReferences
                    })),
                cancellationToken);

            Console.WriteLine(
                $"Collegamenti automatici: totali={result.Total}, " +
                $"PR-commit={result.PullRequestCommits}, " +
                $"PR-file={result.PullRequestFiles}, " +
                $"commit-file={result.CommitFiles}, " +
                $"file-simbolo={result.DeclaredSymbols}, " +
                $"riferimenti testuali={result.TextReferences}.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            failures.Add($"{repository.FullName}: knowledge_links - {IngestionSyncTracker.FormatException(exception)}");
            Console.Error.WriteLine(
                $"Collegamenti automatici di {repository.FullName} non indicizzati: " +
                $"{IngestionSyncTracker.FormatException(exception)}. Verificare che lo script 005 sia stato applicato.");
        }
    }

    private static async Task IndexCSharpSymbolsAsync(
        IOneDataGroveDbContext dbContext,
        GitHubRepositoryReference repository,
        long repositoryId,
        bool forceFull,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var syncTracker = new IngestionSyncTracker(dbContext);
        SyncExecution? execution = null;

        try
        {
            execution = await syncTracker.StartAsync(
                repositoryId,
                "code_symbols",
                forceFull,
                cancellationToken);
            var indexer = new CSharpSymbolIndexer(dbContext);
            var result = await indexer.IndexAsync(repositoryId, cancellationToken);

            await syncTracker.CompleteAsync(
                execution,
                new SyncMetrics(
                    result.CSharpFiles,
                    result.IndexedSymbols,
                    result.IndexedFiles + result.RemovedFiles,
                    JsonSerializer.Serialize(new
                    {
                        result.IndexedFiles,
                        result.UnchangedFiles,
                        result.RemovedFiles,
                        result.PartialFiles
                    })),
                cancellationToken);

            Console.WriteLine(
                $"Struttura C#: file={result.CSharpFiles}, " +
                $"analizzati={result.IndexedFiles}, invariati={result.UnchangedFiles}, " +
                $"rimossi={result.RemovedFiles}, simboli={result.IndexedSymbols}, " +
                $"con errori sintattici={result.PartialFiles}.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            failures.Add($"{repository.FullName}: code_symbols - {IngestionSyncTracker.FormatException(exception)}");
            Console.Error.WriteLine(
                $"Struttura C# di {repository.FullName} non indicizzata: {IngestionSyncTracker.FormatException(exception)}");
        }
    }

    private static async Task<bool> ImportSourceFilesAsync(
        IOneDataGroveDbContext dbContext,
        string githubToken,
        GitHubRepositoryReference repository,
        long repositoryId,
        bool forceFull,
        SourceImportOptions sourceOptions,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var syncTracker = new IngestionSyncTracker(dbContext);
        SyncExecution? execution = null;

        try
        {
            execution = await syncTracker.StartAsync(
                repositoryId,
                "repository_files",
                forceFull,
                cancellationToken);
            var sourceImporter = new GitHubSourceImporter(
                dbContext,
                githubToken,
                sourceOptions);
            var result = await sourceImporter.ImportAsync(
                repositoryId,
                repository.Owner,
                repository.Name,
                forceFull,
                cancellationToken);

            await syncTracker.CompleteAsync(
                execution,
                new SyncMetrics(
                    result.TreeFiles,
                    result.Inserted,
                    result.Updated + result.Restored + result.Deleted,
                    JsonSerializer.Serialize(new
                    {
                        result.EligibleFiles,
                        result.Unchanged,
                        result.Restored,
                        result.Deleted,
                        result.SkippedByPolicy,
                        result.SkippedBinary,
                        result.DownloadStrategy,
                        sourceOptions.MaxFileSizeBytes
                    })),
                cancellationToken);

            Console.WriteLine(
                $"Codice sorgente ({execution.SyncType}, {result.DownloadStrategy}): " +
                $"albero={result.TreeFiles}, idonei={result.EligibleFiles}; " +
                $"inseriti={result.Inserted}, aggiornati={result.Updated}, " +
                $"invariati={result.Unchanged}, ripristinati={result.Restored}, " +
                $"eliminati={result.Deleted}; esclusi={result.SkippedByPolicy}, " +
                $"binari={result.SkippedBinary}.");
            return true;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            failures.Add($"{repository.FullName}: repository_files - {IngestionSyncTracker.FormatException(exception)}");
            Console.Error.WriteLine(
                $"Codice sorgente di {repository.FullName} non sincronizzato: {IngestionSyncTracker.FormatException(exception)}");
            return false;
        }
    }

    private static void ValidateSourceOptions(SourceImportOptions options)
    {
        if (options.MaxFileSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "SourceImport:MaxFileSizeBytes deve essere maggiore di zero.");
        }

        if (options.ArchiveThreshold <= 0)
        {
            throw new InvalidOperationException(
                "SourceImport:ArchiveThreshold deve essere maggiore di zero.");
        }

        if (options.MaxArchiveSizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "SourceImport:MaxArchiveSizeBytes deve essere maggiore di zero.");
        }
    }

    private static async Task ImportIssuesAsync(
        IOneDataGroveDbContext dbContext,
        string githubToken,
        GitHubRepositoryReference repository,
        long repositoryId,
        bool forceFull,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var syncTracker = new IngestionSyncTracker(dbContext);
        SyncExecution? execution = null;

        try
        {
            execution = await syncTracker.StartAsync(
                repositoryId,
                "issues",
                forceFull,
                cancellationToken);
            var issueImporter = new GitHubIssueImporter(dbContext, githubToken);
            var result = await issueImporter.ImportAsync(
                repositoryId,
                repository.Owner,
                repository.Name,
                execution.Since,
                cancellationToken);

            await syncTracker.CompleteAsync(
                execution,
                new SyncMetrics(
                    result.IssuesInserted +
                    result.IssuesUpdated +
                    result.PullRequestsSkipped +
                    result.CommentsInserted +
                    result.CommentsUpdated +
                    result.CommentsRemoved +
                    result.PullRequestCommentsSkipped,
                    result.IssuesInserted + result.CommentsInserted,
                    result.IssuesUpdated + result.CommentsUpdated + result.CommentsRemoved,
                    JsonSerializer.Serialize(new
                    {
                        result.UsersInserted,
                        result.CommentReconciliationIssues,
                        result.CommentsRemoved,
                        result.PullRequestsSkipped,
                        result.PullRequestCommentsSkipped
                    })),
                cancellationToken);

            Console.WriteLine(
                $"Issue ({execution.SyncType}): " +
                $"inserite={result.IssuesInserted}, " +
                $"aggiornate={result.IssuesUpdated}; " +
                $"commenti inseriti={result.CommentsInserted}, " +
                $"aggiornati={result.CommentsUpdated}, " +
                $"rimossi={result.CommentsRemoved}; " +
                $"issue riconciliate={result.CommentReconciliationIssues}; " +
                $"utenti inseriti={result.UsersInserted}; " +
                $"pull request escluse={result.PullRequestsSkipped}, " +
                $"commenti PR esclusi={result.PullRequestCommentsSkipped}.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            failures.Add($"{repository.FullName}: issues - {IngestionSyncTracker.FormatException(exception)}");
            Console.Error.WriteLine(
                $"Issue di {repository.FullName} non sincronizzate: {IngestionSyncTracker.FormatException(exception)}");
        }
    }

    private static async Task ImportPullRequestsAsync(
        IOneDataGroveDbContext dbContext,
        string githubToken,
        GitHubRepositoryReference repository,
        long repositoryId,
        bool forceFull,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var syncTracker = new IngestionSyncTracker(dbContext);
        SyncExecution? execution = null;

        try
        {
            execution = await syncTracker.StartAsync(
                repositoryId,
                "pull_requests",
                forceFull,
                cancellationToken);
            var pullRequestImporter = new GitHubPullRequestImporter(dbContext, githubToken);
            var result = await pullRequestImporter.ImportAsync(
                repositoryId,
                repository.Owner,
                repository.Name,
                execution.Since,
                cancellationToken);

            await syncTracker.CompleteAsync(
                execution,
                new SyncMetrics(
                    result.TotalPullRequests,
                    result.UsersInserted +
                    result.PullRequestsInserted +
                    result.PullRequestFilesInserted +
                    result.CommitsInserted +
                    result.CommitFilesInserted +
                    result.PullRequestCommitLinksInserted,
                    result.PullRequestsUpdated +
                    result.PullRequestFilesUpdated +
                    result.CommitsUpdated +
                    result.PullRequestCommitLinksUpdated,
                    JsonSerializer.Serialize(new
                    {
                        result.PullRequestFilesRemoved,
                        result.CommitFilesRemoved,
                        result.PullRequestCommitLinksRemoved
                    })),
                cancellationToken);

            Console.WriteLine(
                $"Pull request ({execution.SyncType}): " +
                $"totali={result.TotalPullRequests}, " +
                $"inserite={result.PullRequestsInserted}, " +
                $"aggiornate={result.PullRequestsUpdated}; " +
                $"file inseriti={result.PullRequestFilesInserted}, " +
                $"aggiornati={result.PullRequestFilesUpdated}, " +
                $"rimossi={result.PullRequestFilesRemoved}; " +
                $"commit inseriti={result.CommitsInserted}, " +
                $"aggiornati={result.CommitsUpdated}, " +
                $"file commit inseriti={result.CommitFilesInserted}, " +
                $"rimossi={result.CommitFilesRemoved}; " +
                $"relazioni PR-commit inserite={result.PullRequestCommitLinksInserted}.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            throw;
        }
        catch (Exception exception)
        {
            if (execution is not null)
            {
                await syncTracker.FailAsync(execution, exception);
            }
            else
            {
                dbContext.ChangeTracker.Clear();
            }

            failures.Add($"{repository.FullName}: pull_requests - {IngestionSyncTracker.FormatException(exception)}");
            Console.Error.WriteLine(
                $"Pull request di {repository.FullName} non sincronizzate: {IngestionSyncTracker.FormatException(exception)}");
        }
    }

    private static string? ReadArgumentValue(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                return args[index + 1];
            }

            var prefix = option + "=";
            if (args[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return args[index][prefix.Length..];
            }
        }

        return null;
    }
}
