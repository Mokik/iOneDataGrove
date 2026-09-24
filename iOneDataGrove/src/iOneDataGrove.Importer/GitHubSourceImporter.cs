using System.IO.Compression;
using System.Text;
using System.Text.Json;
using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using IOPath = System.IO.Path;

namespace iOneDataGrove.Importer;

internal sealed class GitHubSourceImporter(
    IOneDataGroveDbContext dbContext,
    string token,
    SourceImportOptions options)
{
    private static readonly HashSet<string> AllowedExtensions = new(
        new[]
        {
            ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx",
            ".html", ".htm", ".cshtml", ".razor", ".css", ".scss",
            ".sass", ".less", ".sql", ".md", ".txt", ".xml", ".xaml",
            ".json", ".yaml", ".yml", ".ps1", ".sh", ".bat", ".cmd",
            ".csproj", ".fsproj", ".vbproj", ".sln", ".props", ".targets"
        },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedExtensionlessNames = new(
        new[] { "readme", "license", "dockerfile", "makefile", "procfile" },
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExcludedDirectories = new(
        new[]
        {
            ".git", ".next", ".nuxt", ".idea", ".vs", ".vscode",
            "bin", "obj", "dist", "build", "coverage", "node_modules",
            "packages", "vendor", "bower_components", "artifacts", "testresults",
            "test-results", "playwright-report"
        },
        StringComparer.OrdinalIgnoreCase);

    public async Task<SourceImportResult> ImportAsync(
        long repositoryId,
        string owner,
        string repositoryName,
        bool forceFull,
        CancellationToken cancellationToken = default)
    {
        var repository = await dbContext.Repositories.AsNoTracking()
            .Where(item => item.Id == repositoryId)
            .Select(item => new
            {
                item.DefaultBranch,
                item.HtmlUrl
            })
            .SingleAsync(cancellationToken);

        var existingSnapshot = await dbContext.RepositoryFiles.AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId)
            .Select(item => new ExistingSourceFile(item.Path, item.BlobSha, item.IsDeleted))
            .ToDictionaryAsync(item => item.Path, StringComparer.Ordinal, cancellationToken);

        if (string.IsNullOrWhiteSpace(repository.DefaultBranch))
        {
            var deletedWithoutBranch = await MarkAllDeletedAsync(repositoryId, cancellationToken);
            return new SourceImportResult(
                0, 0, 0, 0, 0, 0, deletedWithoutBranch, 0, 0, "none");
        }

        using var github = new GitHubApiClient(token);
        var tree = await ReadTreeAsync(
            github,
            owner,
            repositoryName,
            repository.DefaultBranch,
            cancellationToken);

        var eligibleFiles = tree.Files
            .Where(item => IsEligible(item.Path, item.Size, options.MaxFileSizeBytes))
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        var skippedByPolicy = tree.Files.Count - eligibleFiles.Count;

        var filesToRead = eligibleFiles.Values
            .Where(item => forceFull ||
                !existingSnapshot.TryGetValue(item.Path, out var existing) ||
                !existing.BlobSha.Equals(item.Sha, StringComparison.Ordinal))
            .ToArray();

        var strategy = "none";
        IReadOnlyDictionary<string, PreparedSourceFile> preparedFiles =
            new Dictionary<string, PreparedSourceFile>(StringComparer.Ordinal);
        var skippedBinary = 0;

        if (filesToRead.Length > 0)
        {
            if (existingSnapshot.Count == 0 || filesToRead.Length >= options.ArchiveThreshold)
            {
                try
                {
                    strategy = "archive";
                    (preparedFiles, skippedBinary) = await ReadFromArchiveAsync(
                        github,
                        owner,
                        repositoryName,
                        repository.DefaultBranch,
                        filesToRead,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or InvalidDataException or InvalidOperationException)
                {
                    strategy = "blobs_fallback";
                    (preparedFiles, skippedBinary) = await ReadBlobsAsync(
                        github,
                        owner,
                        repositoryName,
                        filesToRead,
                        cancellationToken);
                }
            }
            else
            {
                strategy = "blobs";
                (preparedFiles, skippedBinary) = await ReadBlobsAsync(
                    github,
                    owner,
                    repositoryName,
                    filesToRead,
                    cancellationToken);
            }
        }

        var activePaths = eligibleFiles.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var unreadablePath in filesToRead
            .Select(item => item.Path)
            .Where(path => !preparedFiles.ContainsKey(path)))
        {
            activePaths.Remove(unreadablePath);
        }

        var syncedAt = DateTime.UtcNow;
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        var restored = 0;
        var deleted = 0;

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var trackedFiles = await dbContext.RepositoryFiles
            .Where(item => item.RepositoryId == repositoryId)
            .ToDictionaryAsync(item => item.Path, StringComparer.Ordinal, cancellationToken);

        foreach (var treeFile in eligibleFiles.Values)
        {
            if (!activePaths.Contains(treeFile.Path))
            {
                continue;
            }

            if (!preparedFiles.TryGetValue(treeFile.Path, out var prepared))
            {
                var unchangedEntity = trackedFiles[treeFile.Path];
                if (unchangedEntity.IsDeleted)
                {
                    unchangedEntity.IsDeleted = false;
                    unchangedEntity.DeletedAt = null;
                    unchangedEntity.Branch = repository.DefaultBranch;
                    unchangedEntity.HtmlUrl = BuildHtmlUrl(
                        repository.HtmlUrl,
                        repository.DefaultBranch,
                        treeFile.Path);
                    unchangedEntity.SyncedAt = syncedAt;
                    restored++;
                }
                else
                {
                    var expectedHtmlUrl = BuildHtmlUrl(
                        repository.HtmlUrl,
                        repository.DefaultBranch,
                        treeFile.Path);

                    if (!string.Equals(
                            unchangedEntity.Branch,
                            repository.DefaultBranch,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            unchangedEntity.HtmlUrl,
                            expectedHtmlUrl,
                            StringComparison.Ordinal))
                    {
                        unchangedEntity.Branch = repository.DefaultBranch;
                        unchangedEntity.HtmlUrl = expectedHtmlUrl;
                        unchangedEntity.SyncedAt = syncedAt;
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }

                continue;
            }

            if (!trackedFiles.TryGetValue(treeFile.Path, out var entity))
            {
                entity = new RepositoryFile
                {
                    RepositoryId = repositoryId,
                    Path = treeFile.Path
                };
                dbContext.RepositoryFiles.Add(entity);
                trackedFiles.Add(treeFile.Path, entity);
                inserted++;
            }
            else
            {
                updated++;
            }

            entity.FileName = IOPath.GetFileName(treeFile.Path);
            entity.Extension = NormalizeExtension(IOPath.GetExtension(treeFile.Path));
            entity.Language = DetectLanguage(treeFile.Path);
            entity.Branch = repository.DefaultBranch;
            entity.BlobSha = treeFile.Sha;
            entity.SizeBytes = prepared.SizeBytes;
            entity.LineCount = CountLines(prepared.Content);
            entity.Content = prepared.Content;
            entity.ContentEncoding = prepared.Encoding;
            entity.HtmlUrl = BuildHtmlUrl(
                repository.HtmlUrl,
                repository.DefaultBranch,
                treeFile.Path);
            entity.IsDeleted = false;
            entity.DeletedAt = null;
            entity.SyncedAt = syncedAt;
        }

        foreach (var entity in trackedFiles.Values.Where(item =>
            !item.IsDeleted && !activePaths.Contains(item.Path)))
        {
            entity.IsDeleted = true;
            entity.DeletedAt = syncedAt;
            entity.SyncedAt = syncedAt;
            deleted++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SourceImportResult(
            tree.Files.Count,
            eligibleFiles.Count,
            inserted,
            updated,
            unchanged,
            restored,
            deleted,
            skippedByPolicy,
            skippedBinary,
            strategy);
    }

    private async Task<int> MarkAllDeletedAsync(
        long repositoryId,
        CancellationToken cancellationToken)
    {
        var files = await dbContext.RepositoryFiles
            .Where(item => item.RepositoryId == repositoryId && !item.IsDeleted)
            .ToListAsync(cancellationToken);
        var deletedAt = DateTime.UtcNow;
        foreach (var file in files)
        {
            file.IsDeleted = true;
            file.DeletedAt = deletedAt;
            file.SyncedAt = deletedAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return files.Count;
    }

    private static async Task<RepositoryTree> ReadTreeAsync(
        GitHubApiClient github,
        string owner,
        string repositoryName,
        string branch,
        CancellationToken cancellationToken)
    {
        var rawJson = await github.GetAsync(
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}" +
            $"/git/trees/{Uri.EscapeDataString(branch)}?recursive=1",
            cancellationToken);
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;

        if (root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
        {
            throw new InvalidOperationException(
                "L'albero GitHub è troppo grande ed è stato troncato; importazione sorgenti annullata.");
        }

        var files = root.GetProperty("tree")
            .EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "blob")
            .Select(item => new TreeSourceFile(
                item.GetProperty("path").GetString()
                    ?? throw new JsonException("Elemento GitHub senza path."),
                item.GetProperty("sha").GetString()
                    ?? throw new JsonException("Elemento GitHub senza SHA."),
                item.TryGetProperty("size", out var size) ? size.GetInt64() : 0))
            .ToArray();

        return new RepositoryTree(files);
    }

    private async Task<(IReadOnlyDictionary<string, PreparedSourceFile> Files, int SkippedBinary)>
        ReadFromArchiveAsync(
            GitHubApiClient github,
            string owner,
            string repositoryName,
            string branch,
            IReadOnlyCollection<TreeSourceFile> filesToRead,
            CancellationToken cancellationToken)
    {
        var tempPath = IOPath.Combine(
            IOPath.GetTempPath(),
            $"ionedatagrove-source-{Guid.NewGuid():N}.zip");
        try
        {
            await github.DownloadToFileAsync(
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}" +
                $"/zipball/{Uri.EscapeDataString(branch)}",
                tempPath,
                options.MaxArchiveSizeBytes,
                cancellationToken);

            var requested = filesToRead.ToDictionary(item => item.Path, StringComparer.Ordinal);
            var result = new Dictionary<string, PreparedSourceFile>(StringComparer.Ordinal);
            var skippedBinary = 0;
            using var archive = ZipFile.OpenRead(tempPath);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var separatorIndex = entry.FullName.IndexOf('/');
                if (separatorIndex < 0 || entry.Name.Length == 0)
                {
                    continue;
                }

                var relativePath = entry.FullName[(separatorIndex + 1)..];
                if (!requested.TryGetValue(relativePath, out var treeFile))
                {
                    continue;
                }

                var bytes = await ReadEntryAsync(
                    entry,
                    options.MaxFileSizeBytes,
                    cancellationToken);
                var decoded = DecodeText(bytes);
                if (decoded is null)
                {
                    skippedBinary++;
                }
                else
                {
                    result[relativePath] = new PreparedSourceFile(
                        decoded.Value.Content,
                        decoded.Value.Encoding,
                        bytes.LongLength);
                }

                requested.Remove(relativePath);
            }

            if (requested.Count > 0)
            {
                throw new InvalidOperationException(
                    $"L'archivio GitHub non contiene {requested.Count} file attesi.");
            }

            return (result, skippedBinary);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task<(IReadOnlyDictionary<string, PreparedSourceFile> Files, int SkippedBinary)>
        ReadBlobsAsync(
            GitHubApiClient github,
            string owner,
            string repositoryName,
            IReadOnlyCollection<TreeSourceFile> filesToRead,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, PreparedSourceFile>(StringComparer.Ordinal);
        var skippedBinary = 0;

        foreach (var treeFile in filesToRead)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawJson = await github.GetAsync(
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}" +
                $"/git/blobs/{Uri.EscapeDataString(treeFile.Sha)}",
                cancellationToken);
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            var encoding = root.GetProperty("encoding").GetString();
            if (!string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException($"Encoding GitHub non supportato per {treeFile.Path}: {encoding}.");
            }

            var base64 = root.GetProperty("content").GetString()
                ?? throw new JsonException($"Blob GitHub senza contenuto: {treeFile.Path}.");
            var bytes = Convert.FromBase64String(base64);
            if (bytes.LongLength > options.MaxFileSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Il blob {treeFile.Path} supera il limite configurato.");
            }

            var decoded = DecodeText(bytes);
            if (decoded is null)
            {
                skippedBinary++;
                continue;
            }

            result[treeFile.Path] = new PreparedSourceFile(
                decoded.Value.Content,
                decoded.Value.Encoding,
                bytes.LongLength);
        }

        return (result, skippedBinary);
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"Il file {entry.FullName} supera il limite configurato.");
        }

        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private static (string Content, string Encoding)? DecodeText(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return (string.Empty, "utf-8");
        }

        Encoding encoding;
        var preambleLength = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encoding = Encoding.UTF8;
            preambleLength = 3;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encoding = Encoding.Unicode;
            preambleLength = 2;
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encoding = Encoding.BigEndianUnicode;
            preambleLength = 2;
        }
        else
        {
            var nullBytes = bytes.Count(value => value == 0);
            if (nullBytes > 0)
            {
                return null;
            }

            encoding = Encoding.UTF8;
        }

        var content = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        if (encoding == Encoding.UTF8 && content.Contains('\uFFFD'))
        {
            encoding = Encoding.Latin1;
            content = encoding.GetString(bytes);
        }

        var controlCharacters = content.Count(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t' and not '\f');
        if (content.Length > 0 && controlCharacters > Math.Max(4, content.Length / 100))
        {
            return null;
        }

        return (content, encoding.WebName);
    }

    private static bool IsEligible(string path, long size, long maximumBytes)
    {
        if (size < 0 || size > maximumBytes)
        {
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments[..^1].Any(ExcludedDirectories.Contains))
        {
            return false;
        }

        var fileName = segments[^1];
        if (IsSensitiveFile(fileName) || IsGeneratedFile(fileName))
        {
            return false;
        }

        var extension = IOPath.GetExtension(fileName);
        return AllowedExtensions.Contains(extension) ||
            (extension.Length == 0 && AllowedExtensionlessNames.Contains(fileName));
    }

    private static bool IsSensitiveFile(string fileName)
    {
        var normalized = fileName.ToLowerInvariant();
        return normalized == ".env" ||
            normalized.StartsWith(".env.", StringComparison.Ordinal) ||
            normalized is "web.config" or "app.config" or "secrets.json" ||
            (normalized.StartsWith("appsettings", StringComparison.Ordinal) && normalized.EndsWith(".json", StringComparison.Ordinal)) ||
            normalized is "credentials.json" or "credential.json" or "firebase-service-account.json" ||
            normalized.StartsWith("service-account.", StringComparison.Ordinal) ||
            normalized.StartsWith("private-key.", StringComparison.Ordinal) ||
            normalized.EndsWith(".pem", StringComparison.Ordinal) ||
            normalized.EndsWith(".pfx", StringComparison.Ordinal) ||
            normalized.EndsWith(".p12", StringComparison.Ordinal) ||
            normalized.EndsWith(".key", StringComparison.Ordinal);
    }

    private static bool IsGeneratedFile(string fileName)
    {
        var normalized = fileName.ToLowerInvariant();
        return normalized.EndsWith(".min.js", StringComparison.Ordinal) ||
            normalized.EndsWith(".min.css", StringComparison.Ordinal) ||
            normalized.EndsWith(".designer.cs", StringComparison.Ordinal) ||
            normalized.EndsWith(".generated.cs", StringComparison.Ordinal) ||
            normalized.EndsWith(".g.cs", StringComparison.Ordinal) ||
            normalized is "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" or "packages.lock.json";
    }

    private static string? DetectLanguage(string path) =>
        IOPath.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".fs" => "F#",
            ".vb" => "Visual Basic",
            ".js" or ".jsx" => "JavaScript",
            ".ts" or ".tsx" => "TypeScript",
            ".html" or ".htm" or ".cshtml" or ".razor" => "HTML",
            ".css" or ".scss" or ".sass" or ".less" => "CSS",
            ".sql" => "SQL",
            ".md" => "Markdown",
            ".json" => "JSON",
            ".xml" or ".xaml" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" => "XML",
            ".yaml" or ".yml" => "YAML",
            ".ps1" => "PowerShell",
            ".sh" => "Shell",
            ".bat" or ".cmd" => "Batch",
            ".sln" => "Solution",
            ".txt" => "Text",
            _ => null
        };

    private static string? NormalizeExtension(string extension) =>
        string.IsNullOrWhiteSpace(extension) ? null : extension.ToLowerInvariant();

    private static int CountLines(string content) =>
        content.Length == 0 ? 0 : content.Count(character => character == '\n') + 1;

    private static string BuildHtmlUrl(string repositoryUrl, string branch, string path)
    {
        var encodedBranch = string.Join("/", branch.Split('/').Select(Uri.EscapeDataString));
        var encodedPath = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        return $"{repositoryUrl}/blob/{encodedBranch}/{encodedPath}";
    }
}

internal sealed record SourceImportOptions(
    long MaxFileSizeBytes,
    int ArchiveThreshold,
    long MaxArchiveSizeBytes)
{
    public static SourceImportOptions Default { get; } = new(
        512 * 1024,
        100,
        256L * 1024 * 1024);
}

internal sealed record SourceImportResult(
    int TreeFiles,
    int EligibleFiles,
    int Inserted,
    int Updated,
    int Unchanged,
    int Restored,
    int Deleted,
    int SkippedByPolicy,
    int SkippedBinary,
    string DownloadStrategy);

internal sealed record RepositoryTree(IReadOnlyList<TreeSourceFile> Files);

internal sealed record TreeSourceFile(string Path, string Sha, long Size);

internal sealed record ExistingSourceFile(string Path, string BlobSha, bool IsDeleted);

internal sealed record PreparedSourceFile(string Content, string Encoding, long SizeBytes);
