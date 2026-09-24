using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace iOneDataGrove.Importer;

internal sealed class GitHubApiClient : IDisposable
{
    private const int MaximumRetryAttempts = 4;
    private const int MaximumCommitFiles = 3000;
    private const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private readonly HttpClient httpClient;

    public GitHubApiClient(string token)
        : this(token, new HttpClientHandler())
    {
    }

    internal GitHubApiClient(string token, HttpMessageHandler messageHandler)
    {
        httpClient = new HttpClient(messageHandler)
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromMinutes(5)
        };

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("iOneDataGrove/1.0");
    }

    public async Task<IReadOnlyList<string>> GetAllPagesAsync(
        Func<int, string> relativeUrlFactory,
        CancellationToken cancellationToken = default)
    {
        var items = new List<string>();

        for (var page = 1; ; page++)
        {
            using var response = await GetWithRetryAsync(
                relativeUrlFactory(page),
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
            ThrowIfUnsuccessful(response, rawJson, cancellationToken);

            using var document = JsonDocument.Parse(rawJson);
            var pageItems = document.RootElement;

            if (pageItems.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("GitHub non ha restituito un array JSON.");
            }

            foreach (var item in pageItems.EnumerateArray())
            {
                items.Add(item.GetRawText());
            }

            if (!HasNextPage(response))
            {
                return items;
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetDescendingPagesWhileAsync(
        Func<int, string> relativeUrlFactory,
        Func<string, bool> includeItem,
        CancellationToken cancellationToken = default)
    {
        var items = new List<string>();

        for (var page = 1; ; page++)
        {
            using var response = await GetWithRetryAsync(
                relativeUrlFactory(page),
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
            ThrowIfUnsuccessful(response, rawJson, cancellationToken);

            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("GitHub non ha restituito un array JSON.");
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var rawItem = item.GetRawText();
                if (!includeItem(rawItem))
                {
                    return items;
                }

                items.Add(rawItem);
            }

            if (!HasNextPage(response))
            {
                return items;
            }
        }
    }

    public async Task<string> GetAsync(
        string relativeUrl,
        CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(
            relativeUrl,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfUnsuccessful(response, rawJson, cancellationToken);
        return rawJson;
    }

    public async Task<string> GetCommitWithAllFilesAsync(
        string relativeUrl,
        CancellationToken cancellationToken = default)
    {
        JsonObject? aggregate = null;
        JsonArray? aggregateFiles = null;

        for (var page = 1; ; page++)
        {
            var separator = relativeUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var pageUrl = $"{relativeUrl}{separator}per_page=100&page={page}";
            using var response = await GetWithRetryAsync(
                pageUrl,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
            ThrowIfUnsuccessful(response, rawJson, cancellationToken);

            var pageRoot = JsonNode.Parse(rawJson)?.AsObject()
                ?? throw new JsonException("GitHub non ha restituito un commit JSON valido.");
            var pageFiles = pageRoot["files"] as JsonArray;

            if (aggregate is null)
            {
                aggregate = pageRoot.DeepClone().AsObject();
                aggregateFiles = new JsonArray();
                aggregate["files"] = aggregateFiles;
            }

            if (pageFiles is not null)
            {
                foreach (var file in pageFiles)
                {
                    aggregateFiles!.Add(file?.DeepClone());
                }
            }

            if (aggregateFiles!.Count >= MaximumCommitFiles)
            {
                return await ReconstructCommitFilesAsync(relativeUrl, aggregate, cancellationToken);
            }

            if (!HasNextPage(response))
            {
                return aggregate.ToJsonString();
            }
        }
    }

    public async Task<IReadOnlyList<string>> GetPullRequestFilesAsync(
        string pullBasePath,
        string rawDetail,
        CancellationToken cancellationToken = default)
    {
        using var detail = JsonDocument.Parse(rawDetail);
        var root = detail.RootElement;
        var expectedFiles = root.GetProperty("changed_files").GetInt32();
        var apiFiles = await GetAllPagesAsync(
            page => $"{pullBasePath}/files?per_page=100&page={page}",
            cancellationToken);

        if (apiFiles.Count == expectedFiles)
        {
            return apiFiles;
        }

        if (apiFiles.Count < MaximumCommitFiles || expectedFiles <= MaximumCommitFiles)
        {
            return apiFiles;
        }

        var repositoryPath = pullBasePath[..pullBasePath.IndexOf("/pulls/", StringComparison.Ordinal)];
        var baseSha = root.GetProperty("base").GetProperty("sha").GetString()!;
        var headSha = root.GetProperty("head").GetProperty("sha").GetString()!;
        var baseTree = await ReadTreeAsync(repositoryPath, baseSha, cancellationToken);
        var headTree = await ReadTreeAsync(repositoryPath, headSha, cancellationToken);
        var reconstructed = ReconstructChangedFiles(baseTree, headTree);

        Console.WriteLine(
            $"Pull request oltre il limite GitHub: ricostruiti {reconstructed.Count} " +
            "file dagli alberi Git; statistiche di riga e patch non disponibili.");
        return reconstructed;
    }

    // GitHub caps commit diffs at 3,000 files. Recover the complete path set from
    // immutable Git trees instead; unavailable line statistics stay null.
    private async Task<string> ReconstructCommitFilesAsync(string commitUrl, JsonObject commit, CancellationToken ct)
    {
        var repositoryPath = commitUrl[..commitUrl.IndexOf("/commits/", StringComparison.Ordinal)];
        var treeSha = commit["commit"]!["tree"]!["sha"]!.GetValue<string>();
        var current = await ReadTreeAsync(repositoryPath, treeSha, ct);
        var previous = new Dictionary<string, (string Sha, string Mode)>(StringComparer.Ordinal);
        var parentSha = (commit["parents"] as JsonArray)?.FirstOrDefault()?["sha"]?.GetValue<string>();
        if (parentSha is not null)
        {
            using var parent = JsonDocument.Parse(await GetAsync($"{repositoryPath}/git/commits/{parentSha}", ct));
            previous = await ReadTreeAsync(
                repositoryPath,
                parent.RootElement.GetProperty("tree").GetProperty("sha").GetString()!,
                ct);
        }
        var files = new JsonArray();
        foreach (var path in previous.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var had = previous.TryGetValue(path, out var before);
            var has = current.TryGetValue(path, out var after);
            if (had && has && before == after) continue;
            files.Add(new JsonObject {
                ["filename"] = path, ["status"] = !had ? "added" : !has ? "removed" : "modified",
                ["sha"] = has ? after.Sha : before.Sha,
                ["additions"] = null, ["deletions"] = null, ["changes"] = null
            });
        }
        commit["files"] = files;
        commit["stats"] = new JsonObject { ["additions"] = null, ["deletions"] = null, ["total"] = null };
        commit["files_reconstructed_from_trees"] = true;
        commit["comparison_parent"] = parentSha;
        Console.WriteLine($"Commit oltre il limite diff GitHub: ricostruiti {files.Count} file dagli alberi Git; statistiche di riga non disponibili.");
        return commit.ToJsonString();
    }

    private async Task<Dictionary<string, (string Sha, string Mode)>> ReadTreeAsync(
        string repositoryPath,
        string sha,
        CancellationToken cancellationToken)
    {
        if (string.Equals(sha, EmptyTreeSha, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, (string Sha, string Mode)>(StringComparer.Ordinal);
        }

        using var tree = JsonDocument.Parse(await GetAsync(
            $"{repositoryPath}/git/trees/{sha}?recursive=1",
            cancellationToken));
        var root = tree.RootElement;
        if (root.GetProperty("truncated").GetBoolean())
            throw new InvalidOperationException("Albero Git troncato: impossibile garantire l'elenco completo dei file.");
        return root.GetProperty("tree").EnumerateArray()
            .Where(element => element.GetProperty("type").GetString() != "tree")
            .ToDictionary(
                element => element.GetProperty("path").GetString()!,
                element => (
                    element.GetProperty("sha").GetString()!,
                    element.GetProperty("mode").GetString()!),
                StringComparer.Ordinal);
    }

    internal static IReadOnlyList<string> ReconstructChangedFiles(
        IReadOnlyDictionary<string, (string Sha, string Mode)> before,
        IReadOnlyDictionary<string, (string Sha, string Mode)> after)
    {
        var files = new List<JsonObject>();
        var removed = before
            .Where(pair => !after.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        var added = after
            .Where(pair => !before.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        var matchedAdded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var oldFile in removed)
        {
            var renamed = added.FirstOrDefault(candidate =>
                !matchedAdded.Contains(candidate.Key) &&
                candidate.Value == oldFile.Value);
            if (!string.IsNullOrEmpty(renamed.Key))
            {
                matchedAdded.Add(renamed.Key);
                files.Add(CreateChangedFile(
                    renamed.Key,
                    "renamed",
                    renamed.Value.Sha,
                    oldFile.Key));
            }
            else
            {
                files.Add(CreateChangedFile(oldFile.Key, "removed", oldFile.Value.Sha));
            }
        }

        foreach (var newFile in added.Where(pair => !matchedAdded.Contains(pair.Key)))
            files.Add(CreateChangedFile(newFile.Key, "added", newFile.Value.Sha));

        foreach (var path in before.Keys.Intersect(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            if (before[path] != after[path])
                files.Add(CreateChangedFile(path, "modified", after[path].Sha));

        return files
            .OrderBy(file => file["filename"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(file => file.ToJsonString())
            .ToArray();

        static JsonObject CreateChangedFile(
            string filename,
            string status,
            string sha,
            string? previousFilename = null) => new()
        {
            ["filename"] = filename,
            ["previous_filename"] = previousFilename,
            ["status"] = status,
            ["sha"] = sha,
            ["additions"] = null,
            ["deletions"] = null,
            ["changes"] = null,
            ["patch"] = null
        };
    }

    // null means the previous head is no longer an ancestor (or no longer available).
    internal async Task<IReadOnlyList<string>?> GetComparedCommitShasAsync(
        string repositoryPath, string previousHead, string head, CancellationToken ct = default)
    {
        var shas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? expected = null;
        for (var page = 1; ; page++)
        {
            using var response = await GetWithRetryAsync(
                $"{repositoryPath}/compare/{previousHead}...{head}?per_page=100&page={page}",
                HttpCompletionOption.ResponseContentRead, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            ThrowIfUnsuccessful(response, raw, ct);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString();
            if (status is "diverged" or "behind") return null;
            if (status is not ("ahead" or "identical"))
                throw new InvalidOperationException("Stato confronto commit GitHub non riconosciuto.");
            var total = root.GetProperty("total_commits").GetInt32();
            expected ??= total;
            if (expected != total) throw new InvalidOperationException("Confronto commit incoerente tra pagine.");
            foreach (var commit in root.GetProperty("commits").EnumerateArray())
                shas.Add(commit.GetProperty("sha").GetString()!);
            if (!HasNextPage(response))
            {
                if (shas.Count != expected)
                    throw new InvalidOperationException($"Confronto commit incompleto: attesi {expected}, ricevuti {shas.Count}.");
                return shas.ToArray();
            }
        }
    }

    public async Task DownloadToFileAsync(
        string relativeUrl,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync(
            relativeUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            ThrowIfUnsuccessful(response, errorBody, cancellationToken);
        }

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > maximumBytes)
        {
            throw new InvalidOperationException(
                $"Archivio GitHub troppo grande ({contentLength} byte; limite {maximumBytes}).");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalBytes += bytesRead;
            if (totalBytes > maximumBytes)
            {
                throw new InvalidOperationException(
                    $"Archivio GitHub oltre il limite configurato di {maximumBytes} byte.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(
        string relativeUrl,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClient.GetAsync(
                    relativeUrl,
                    completionOption,
                    cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaximumRetryAttempts)
            {
                await Task.Delay(GetExponentialDelay(attempt), cancellationToken);
                continue;
            }
            catch (TaskCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                attempt < MaximumRetryAttempts)
            {
                await Task.Delay(GetExponentialDelay(attempt), cancellationToken);
                continue;
            }

            var rateLimitDelay = GetPrimaryRateLimitDelay(
                response,
                DateTimeOffset.UtcNow);
            if (rateLimitDelay is TimeSpan waitForRateLimit)
            {
                var resumeAt = DateTimeOffset.Now.Add(waitForRateLimit);
                response.Dispose();
                Console.WriteLine(
                    $"Limite API GitHub esaurito; importazione in pausa fino alle " +
                    $"{resumeAt:HH:mm:ss} ({Math.Ceiling(waitForRateLimit.TotalMinutes)} minuti). " +
                    $"Premi Ctrl+C per interrompere in modo controllato.");
                await Task.Delay(waitForRateLimit, cancellationToken);
                attempt = -1;
                continue;
            }

            if (response.IsSuccessStatusCode ||
                attempt >= MaximumRetryAttempts ||
                !IsTransient(response))
            {
                return response;
            }

            var delay = GetRetryDelay(response, attempt);
            response.Dispose();
            Console.WriteLine(
                $"GitHub temporaneamente non disponibile; nuovo tentativo tra " +
                $"{Math.Ceiling(delay.TotalSeconds)} secondi " +
                $"({attempt + 1}/{MaximumRetryAttempts}).");
            await Task.Delay(delay, cancellationToken);
        }
    }

    internal static TimeSpan? GetPrimaryRateLimitDelay(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden ||
            TryGetHeader(response, "X-RateLimit-Remaining") != "0" ||
            !long.TryParse(
                TryGetHeader(response, "X-RateLimit-Reset"),
                out var resetSeconds))
        {
            return null;
        }

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
        var delay = resetAt - now + TimeSpan.FromSeconds(5);
        return delay < TimeSpan.FromSeconds(5)
            ? TimeSpan.FromSeconds(5)
            : delay;
    }

    private static bool IsTransient(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500)
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        return response.Headers.RetryAfter is not null ||
               TryGetHeader(response, "X-RateLimit-Remaining") == "0";
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta)
        {
            return LimitDelay(delta);
        }

        if (retryAfter?.Date is DateTimeOffset retryDate)
        {
            return LimitDelay(retryDate - DateTimeOffset.UtcNow);
        }

        if (TryGetHeader(response, "X-RateLimit-Remaining") == "0" &&
            long.TryParse(TryGetHeader(response, "X-RateLimit-Reset"), out var resetSeconds))
        {
            var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
            return LimitDelay(resetAt - DateTimeOffset.UtcNow);
        }

        return GetExponentialDelay(attempt);
    }

    private static TimeSpan GetExponentialDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));

    private static TimeSpan LimitDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return delay > TimeSpan.FromSeconds(60)
            ? TimeSpan.FromSeconds(60)
            : delay;
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;

    private static bool HasNextPage(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Link", out var values) &&
        values.SelectMany(value => value.Split(','))
            .Any(link => link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase));

    private static void ThrowIfUnsuccessful(
        HttpResponseMessage response,
        string responseBody,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var compactBody = string.Join(
            ' ',
            responseBody.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compactBody.Length > 500)
        {
            compactBody = compactBody[..500] + "...";
        }

        var rateRemaining = TryGetHeader(response, "X-RateLimit-Remaining") ?? "n/d";
        var rateReset = TryGetHeader(response, "X-RateLimit-Reset") ?? "n/d";
        throw new HttpRequestException(
            $"GitHub ha restituito {(int)response.StatusCode} {response.ReasonPhrase} " +
            $"per {response.RequestMessage?.RequestUri}. " +
            $"RateLimitRemaining={rateRemaining}; RateLimitReset={rateReset}; " +
            $"Risposta={compactBody}",
            null,
            response.StatusCode);
    }

    public void Dispose() => httpClient.Dispose();
}
