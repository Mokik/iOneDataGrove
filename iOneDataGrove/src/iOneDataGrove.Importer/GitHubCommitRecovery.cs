using System.Text.Json;
using System.Text.RegularExpressions;
using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

// Explicit recovery of a commit that was not acquired through a pull request.
// Does not advance the watermark for repository-wide commit synchronization.
internal sealed class GitHubCommitRecovery(IOneDataGroveDbContext db, string token)
{
    internal static void ValidateSha(string sha)
    {
        if (!Regex.IsMatch(sha, "\\A[0-9a-fA-F]{40}\\z"))
            throw new ArgumentException("Specificare lo SHA completo del commit (40 caratteri esadecimali).");
    }

    public async Task<long> ImportAsync(string fullName, string sha, CancellationToken ct, GitHubApiClient? client = null)
    {
        ValidateSha(sha);
        sha = sha.ToLowerInvariant();
        var repository = await db.Repositories.SingleAsync(r => r.FullName == fullName, ct);
        if (repository.IsExcluded || !repository.IsSyncEnabled)
            throw new InvalidOperationException("Il repository è escluso o la sincronizzazione è sospesa.");
        var parts = repository.FullName.Split('/');
        using var ownedClient = client is null ? new GitHubApiClient(token) : null;
        var github = client ?? ownedClient!;
        var raw = await github.GetCommitWithAllFilesAsync(
            $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/commits/{sha}", ct);
        return await SaveAsync(repository, sha, raw, ct);
    }

    internal async Task<long> SaveAsync(Repository repository, string sha, string raw, CancellationToken ct)
    {
        ValidateSha(sha);
        sha = sha.ToLowerInvariant();
        var candidate = ParseCommit(raw, sha);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.Commits.Include(c => c.CommitFiles)
            .SingleOrDefaultAsync(c => c.RepositoryId == repository.Id && c.Sha == sha, ct);
        var commit = existing ?? new Commit { RepositoryId = repository.Id, Sha = sha };
        commit.Message = candidate.Message;
        commit.AuthorName = candidate.AuthorName;
        commit.AuthorEmail = candidate.AuthorEmail;
        commit.CommitterName = candidate.CommitterName;
        commit.CommitterEmail = candidate.CommitterEmail;
        commit.AuthoredAt = candidate.AuthoredAt;
        commit.CommittedAt = candidate.CommittedAt;
        commit.HtmlUrl = candidate.HtmlUrl;
        commit.Additions = candidate.Additions;
        commit.Deletions = candidate.Deletions;
        commit.FilesChanged = candidate.FilesChanged;
        commit.RawJson = raw;
        commit.SyncedAt = DateTime.UtcNow;
        using var document = JsonDocument.Parse(raw);
        commit.AuthorUser = await FindUserAsync(document.RootElement, "author", ct);
        commit.CommitterUser = await FindUserAsync(document.RootElement, "committer", ct);
        if (existing is null) db.Commits.Add(commit);
        var remotePaths = candidate.CommitFiles.Select(f => f.Filename).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in commit.CommitFiles.Where(f => !remotePaths.Contains(f.Filename)).ToArray())
        {
            db.CommitFiles.Remove(stale);
            commit.CommitFiles.Remove(stale);
        }
        var existingPaths = commit.CommitFiles.Select(f => f.Filename).ToHashSet(StringComparer.Ordinal);
        foreach (var file in candidate.CommitFiles)
            if (existingPaths.Add(file.Filename)) commit.CommitFiles.Add(file);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return commit.Id;
    }

    private async Task<User?> FindUserAsync(JsonElement root, string key, CancellationToken ct)
    {
        if (!root.TryGetProperty(key, out var user) || user.ValueKind != JsonValueKind.Object) return null;
        var id = user.GetProperty("id").GetInt64();
        var existing = db.Users.Local.FirstOrDefault(u => u.GithubId == id)
            ?? await db.Users.SingleOrDefaultAsync(u => u.GithubId == id, ct);
        if (existing is not null) return existing;
        var added = new User {
            GithubId = id, Login = user.GetProperty("login").GetString()!,
            NodeId = Text(user, "node_id"), AvatarUrl = Text(user, "avatar_url"),
            HtmlUrl = Text(user, "html_url"), UserType = Text(user, "type"),
            SyncedAt = DateTime.UtcNow, RawJson = user.GetRawText()
        };
        db.Users.Add(added);
        return added;
    }

    internal static Commit ParseCommit(string raw, string expectedSha)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!string.Equals(root.GetProperty("sha").GetString(), expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Lo SHA restituito da GitHub non corrisponde al commit richiesto.");
        var detail = root.GetProperty("commit");
        var author = detail.GetProperty("author");
        var committer = detail.GetProperty("committer");
        var stats = root.GetProperty("stats");
        var commit = new Commit {
            Sha = expectedSha.ToLowerInvariant(), Message = detail.GetProperty("message").GetString()!,
            AuthorName = Text(author, "name"), AuthorEmail = Text(author, "email"),
            CommitterName = Text(committer, "name"), CommitterEmail = Text(committer, "email"),
            AuthoredAt = Date(author), CommittedAt = Date(committer),
            HtmlUrl = Text(root, "html_url"), RawJson = raw,
            Additions = Number(stats, "additions"),
            Deletions = Number(stats, "deletions")
        };
        foreach (var file in root.GetProperty("files").EnumerateArray())
            commit.CommitFiles.Add(new CommitFile {
                Filename = file.GetProperty("filename").GetString()!,
                PreviousFilename = Text(file, "previous_filename"), Status = Text(file, "status"),
                Additions = Number(file, "additions"),
                Deletions = Number(file, "deletions"),
                Changes = Number(file, "changes"),
                Patch = Text(file, "patch"), RawJson = file.GetRawText()
            });
        commit.FilesChanged = commit.CommitFiles.Count;
        return commit;
    }

    private static string? Text(JsonElement item, string key) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static int? Number(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    private static DateTime? Date(JsonElement item) => Text(item, "date") is { } value
        ? DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime : null;
}
