using System.Net.Http.Headers;
using System.Text.Json;
using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace iOneDataGrove.Importer;

internal sealed class GitHubRepositoryImporter(
    IOneDataGroveDbContext dbContext,
    string token)
{
    public async Task<RepositoryImportResult> ImportAsync(
        string owner,
        string repositoryName,
        CancellationToken cancellationToken = default)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("iOneDataGrove/1.0");

        using var response = await httpClient.GetAsync(
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repositoryName)}",
            cancellationToken);

        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub ha restituito {(int)response.StatusCode} {response.ReasonPhrase} " +
                $"per {owner}/{repositoryName}.");
        }

        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var ownerJson = root.GetProperty("owner");
        var syncedAt = DateTime.UtcNow;

        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var ownerGithubId = ownerJson.GetProperty("id").GetInt64();
        var ownerEntity = await dbContext.Users.SingleOrDefaultAsync(
            user => user.GithubId == ownerGithubId,
            cancellationToken);
        var ownerInserted = ownerEntity is null;

        ownerEntity ??= new User { GithubId = ownerGithubId };
        ownerEntity.NodeId = GetOptionalString(ownerJson, "node_id");
        ownerEntity.Login = ownerJson.GetProperty("login").GetString()!;
        ownerEntity.AvatarUrl = GetOptionalString(ownerJson, "avatar_url");
        ownerEntity.HtmlUrl = GetOptionalString(ownerJson, "html_url");
        ownerEntity.UserType = GetOptionalString(ownerJson, "type");
        ownerEntity.SiteAdmin = GetOptionalBoolean(ownerJson, "site_admin");
        ownerEntity.SyncedAt = syncedAt;
        ownerEntity.RawJson = ownerJson.GetRawText();

        if (ownerInserted)
        {
            dbContext.Users.Add(ownerEntity);
        }

        var repositoryGithubId = root.GetProperty("id").GetInt64();
        var repositoryEntity = await dbContext.Repositories.SingleOrDefaultAsync(
            repository => repository.GithubId == repositoryGithubId,
            cancellationToken);
        var repositoryInserted = repositoryEntity is null;

        repositoryEntity ??= new Repository
        {
            GithubId = repositoryGithubId,
            IsSyncEnabled = true
        };
        repositoryEntity.NodeId = GetOptionalString(root, "node_id");
        repositoryEntity.OwnerUser = ownerEntity;
        repositoryEntity.Name = root.GetProperty("name").GetString()!;
        repositoryEntity.FullName = root.GetProperty("full_name").GetString()!;
        repositoryEntity.Description = GetOptionalString(root, "description");
        repositoryEntity.HtmlUrl = root.GetProperty("html_url").GetString()!;
        repositoryEntity.IsPrivate = root.GetProperty("private").GetBoolean();
        repositoryEntity.IsFork = root.GetProperty("fork").GetBoolean();
        repositoryEntity.IsArchived = root.GetProperty("archived").GetBoolean();
        repositoryEntity.IsDisabled = root.GetProperty("disabled").GetBoolean();
        repositoryEntity.DefaultBranch = GetOptionalString(root, "default_branch");
        repositoryEntity.PrimaryLanguage = GetOptionalString(root, "language");
        repositoryEntity.CreatedAt = GetOptionalUtcDateTime(root, "created_at");
        repositoryEntity.UpdatedAt = GetOptionalUtcDateTime(root, "updated_at");
        repositoryEntity.PushedAt = GetOptionalUtcDateTime(root, "pushed_at");
        repositoryEntity.SyncedAt = syncedAt;
        repositoryEntity.RawJson = rawJson;

        if (repositoryInserted)
        {
            dbContext.Repositories.Add(repositoryEntity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RepositoryImportResult(
            repositoryEntity.Id,
            repositoryEntity.FullName,
            ownerInserted,
            repositoryInserted,
            syncedAt);
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

internal sealed record RepositoryImportResult(
    long RepositoryId,
    string FullName,
    bool OwnerInserted,
    bool RepositoryInserted,
    DateTime SyncedAt);
