using System.Text.Json;

namespace iOneDataGrove.Importer;

internal sealed class GitHubRepositoryDiscovery(string token)
{
    public async Task<IReadOnlyList<GitHubRepositoryReference>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        using var github = new GitHubApiClient(token);
        var repositoriesJson = await github.GetAllPagesAsync(
            page => "user/repos" +
                    "?affiliation=owner%2Ccollaborator%2Corganization_member" +
                    "&sort=full_name&direction=asc&per_page=100" +
                    $"&page={page}",
            cancellationToken);

        var repositories = new List<GitHubRepositoryReference>(repositoriesJson.Count);

        foreach (var rawJson in repositoriesJson)
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            var owner = root.GetProperty("owner").GetProperty("login").GetString();
            var name = root.GetProperty("name").GetString();
            var fullName = root.GetProperty("full_name").GetString();

            if (string.IsNullOrWhiteSpace(owner) ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(fullName))
            {
                throw new JsonException(
                    "GitHub ha restituito un repository senza owner, name o full_name.");
            }

            repositories.Add(new GitHubRepositoryReference(
                root.GetProperty("id").GetInt64(),
                owner,
                name,
                fullName));
        }

        return repositories
            .DistinctBy(repository => repository.GithubId)
            .OrderBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed record GitHubRepositoryReference(
    long GithubId,
    string Owner,
    string Name,
    string FullName);
