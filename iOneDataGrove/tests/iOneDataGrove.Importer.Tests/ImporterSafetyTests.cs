using System.Net;
using System.Text.Json;
using iOneDataGrove.Importer;
using iOneDataGrove.Persistence.Data;
using iOneDataGrove.Persistence.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iOneDataGrove.Importer.Tests;

[TestClass]
public sealed class ImporterSafetyTests
{
    [TestMethod]
    public void IncrementalWatermarkUsesRunStartInsteadOfCompletionTime()
    {
        var startedAt = new DateTime(2026, 8, 27, 10, 15, 0, DateTimeKind.Utc);
        var execution = new SyncExecution(
            new SyncState(),
            new SyncRun { StartedAt = startedAt },
            null,
            "incremental");

        var watermark = IngestionSyncTracker.GetIncrementalWatermark(execution);

        Assert.AreEqual(startedAt, watermark);
    }

    [TestMethod]
    public void PullRequestCompletenessRejectsMissingFiles()
    {
        const string detail = """
            { "changed_files": 3, "commits": 2 }
            """;

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            GitHubPullRequestImporter.ValidateCompleteness(42, detail, 2, 2));

        StringAssert.Contains(exception.Message, "dichiara 3 file");
    }

    [TestMethod]
    public void PullRequestCompletenessRejectsMissingCommits()
    {
        const string detail = """
            { "changed_files": 3, "commits": 251 }
            """;

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            GitHubPullRequestImporter.ValidateCompleteness(42, detail, 3, 250));

        StringAssert.Contains(exception.Message, "dichiara 251 commit");
    }

    [TestMethod]
    public void IncrementalIssuesReconcileBothMissingAndExcessComments()
    {
        var expected = new Dictionary<int, int>
        {
            [22] = 7,
            [23] = 1,
            [24] = 2,
            [25] = 0
        };
        var imported = new Dictionary<int, int>
        {
            [22] = 1,
            [23] = 1,
            [24] = 3
        };

        var issueNumbers = GitHubIssueImporter.GetCommentReconciliationIssueNumbers(
            expected,
            imported);

        CollectionAssert.AreEqual(new[] { 22, 24 }, issueNumbers.ToArray());
    }

    [TestMethod]
    public void CommentReconciliationRemovesOnlyIdsMissingFromGitHub()
    {
        var staleIds = GitHubIssueImporter.GetStaleCommentIds(
            new long[] { 101, 102, 103 },
            new HashSet<long> { 101, 103 });

        CollectionAssert.AreEqual(new long[] { 102 }, staleIds.ToArray());
    }

    [TestMethod]
    public void KnowledgeLinksRecognizeReferencesAndClosingKeywords()
    {
        var references = KnowledgeLinkIndexer.ExtractReferences(
            "Rif. #12. Fixes #34, resolves iOneSolutionsSrl/iOneGavio#56, " +
            "Chiude #78 e chiudono #90.",
            "iOneSolutionsSrl/iOneGavio");

        Assert.AreEqual(5, references.Count);
        Assert.AreEqual("references", references.Single(item => item.Number == 12).RelationType);
        Assert.AreEqual("closes", references.Single(item => item.Number == 34).RelationType);
        Assert.AreEqual("closes", references.Single(item => item.Number == 56).RelationType);
        Assert.AreEqual("closes", references.Single(item => item.Number == 78).RelationType);
        Assert.AreEqual("closes", references.Single(item => item.Number == 90).RelationType);
    }

    [TestMethod]
    public void KnowledgeLinksIgnoreReferencesQualifiedForAnotherRepository()
    {
        var references = KnowledgeLinkIndexer.ExtractReferences(
            "Vedi other/Repository#18 e #19.",
            "iOneSolutionsSrl/iOneGavio");

        CollectionAssert.AreEqual(
            new[] { 19 },
            references.Select(item => item.Number).ToArray());
    }

    [TestMethod]
    public async Task PaginationFollowsGitHubLinkHeader()
    {
        var requestCount = 0;
        using var client = CreateClient((_, call) =>
        {
            var response = JsonResponse(call == 1 ? "[{\"id\":1}]" : "[{\"id\":2}]");
            if (call == 1)
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://api.github.com/items?page=2>; rel=\"next\"");
            }

            requestCount = call;
            return response;
        });

        var items = await client.GetAllPagesAsync(page => $"items?page={page}");

        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(2, requestCount);
    }

    [TestMethod]
    public async Task CommitPaginationMergesFilesFromEveryPage()
    {
        using var client = CreateClient((_, call) =>
        {
            var response = JsonResponse(call == 1
                ? "{\"sha\":\"abc\",\"files\":[{\"filename\":\"a.cs\"}]}"
                : "{\"sha\":\"abc\",\"files\":[{\"filename\":\"b.cs\"}]}");
            if (call == 1)
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://api.github.com/commit?page=2>; rel=\"next\"");
            }

            return response;
        });

        var json = await client.GetCommitWithAllFilesAsync("commit");
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual(
            2,
            document.RootElement.GetProperty("files").GetArrayLength());
    }

    [TestMethod]
    public async Task BranchComparisonReadsEveryPageWithoutDateFiltering()
    {
        using var client = CreateClient((request, call) =>
        {
            StringAssert.Contains(request.RequestUri!.Query, "per_page=100");
            Assert.IsFalse(request.RequestUri.Query.Contains("since="));
            var response = JsonResponse(call == 1
                ? "{\"status\":\"ahead\",\"total_commits\":2,\"commits\":[{\"sha\":\"new\"}]}"
                : "{\"status\":\"ahead\",\"total_commits\":2,\"commits\":[{\"sha\":\"old-merged-today\"}]}");
            if (call == 1) response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/compare?page=2>; rel=\"next\"");
            return response;
        });
        var shas = await client.GetComparedCommitShasAsync("repos/o/r", "base", "head");
        CollectionAssert.AreEquivalent(new[] { "new", "old-merged-today" }, shas!.ToArray());
    }

    [TestMethod]
    public async Task BranchComparisonRejectsTruncatedResults()
    {
        using var client = CreateClient((_, _) => JsonResponse(
            "{\"status\":\"ahead\",\"total_commits\":2,\"commits\":[{\"sha\":\"one\"}]}"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await client.GetComparedCommitShasAsync("repos/o/r", "base", "head"));
    }

    [TestMethod]
    public async Task RewrittenOrUnavailableHistoryRequiresBackfill()
    {
        foreach (var status in new[] { "diverged", "behind", "not-found" })
        {
            using var client = CreateClient((_, _) => status == "not-found"
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : JsonResponse($"{{\"status\":\"{status}\"}}"));
            Assert.IsNull(await client.GetComparedCommitShasAsync("repos/o/r", "base", "head"));
        }
    }

    [TestMethod]
    public void PreviouslyImportedPrCommitsNeedCompleteFileDetails()
    {
        Assert.IsFalse(GitHubBranchCommitImporter.IsComplete(null, 0));
        Assert.IsFalse(GitHubBranchCommitImporter.IsComplete(3, 2));
        Assert.IsFalse(GitHubBranchCommitImporter.IsComplete(1, 2));
        Assert.IsTrue(GitHubBranchCommitImporter.IsComplete(0, 0));
        Assert.IsTrue(GitHubBranchCommitImporter.IsComplete(2, 2));
        Assert.IsNull(GitHubBranchCommitImporter.ReadCursor("an old non-JSON cursor"));
        Assert.IsNull(GitHubBranchCommitImporter.ReadCursor("{\"Branch\":\"main\",\"Head\":\"main\"}"));
    }

    [TestMethod]
    public async Task OversizedInitialCommitUsesCompleteTreeAndKeepsUnknownStatisticsNull()
    {
        var sha = new string('a', 40);
        using var client = CreateClient((request, _) => request.RequestUri!.AbsolutePath.Contains("/git/trees/")
            ? JsonResponse(JsonSerializer.Serialize(new {
                truncated = false,
                tree = Enumerable.Range(0, 3001).Select(i => new { path = $"f{i}.cs", type = "blob", sha = "blob", mode = "100644" })
            }))
            : JsonResponse(JsonSerializer.Serialize(new {
                sha,
                commit = new { tree = new { sha = "tree" }, message = "Initial commit", author = (object?)null, committer = (object?)null },
                parents = Array.Empty<object>(),
                files = Enumerable.Range(0, 3000).Select(i => new { filename = $"f{i}.cs" })
            })));
        var raw = await client.GetCommitWithAllFilesAsync($"repos/o/r/commits/{sha}");
        var commit = GitHubCommitRecovery.ParseCommit(raw, sha);
        Assert.AreEqual(3001, commit.FilesChanged);
        Assert.IsNull(commit.Additions);
        Assert.IsTrue(commit.CommitFiles.All(f => f.Status == "added" && f.Patch is null && f.Additions is null));
        StringAssert.Contains(raw, "files_reconstructed_from_trees");
    }

    [TestMethod]
    public async Task OversizedCommitRejectsTruncatedTree()
    {
        using var client = CreateClient((request, _) => request.RequestUri!.AbsolutePath.Contains("/git/trees/")
            ? JsonResponse("{\"truncated\":true,\"tree\":[]}")
            : JsonResponse(JsonSerializer.Serialize(new {
                commit = new { tree = new { sha = "tree" } },
                files = Enumerable.Range(0, 3000).Select(i => new { filename = $"f{i}" })
            })));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await client.GetCommitWithAllFilesAsync("repos/o/r/commits/sha"));
    }

    [TestMethod]
    public async Task TreeFallbackComparesFirstParentIncludingRemovedFilesAndModeChanges()
    {
        using var client = CreateClient((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/git/commits/")) return JsonResponse("{\"tree\":{\"sha\":\"old\"}}");
            if (path.Contains("/git/trees/"))
            {
                var isOld = path.EndsWith("/old", StringComparison.Ordinal);
                return JsonResponse(JsonSerializer.Serialize(new {
                    truncated = false, tree = new[] {
                        new { path = "unchanged.cs", type = "blob", sha = "same", mode = "100644" },
                        new { path = isOld ? "removed.cs" : "added.cs", type = "blob", sha = "same", mode = "100644" },
                        new { path = "mode.sh", type = "blob", sha = "same", mode = isOld ? "100644" : "100755" }
                    }
                }));
            }
            return JsonResponse(JsonSerializer.Serialize(new {
                commit = new { tree = new { sha = "new" } },
                parents = new[] { new { sha = "parent" }, new { sha = "second-parent" } },
                files = Enumerable.Range(0, 3000).Select(i => new { filename = $"f{i}" })
            }));
        });
        using var result = JsonDocument.Parse(await client.GetCommitWithAllFilesAsync("repos/o/r/commits/head"));
        var files = result.RootElement.GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("filename").GetString()!, f => f.GetProperty("status").GetString());
        Assert.AreEqual(3, files.Count);
        Assert.AreEqual("removed", files["removed.cs"]);
        Assert.AreEqual("added", files["added.cs"]);
        Assert.AreEqual("modified", files["mode.sh"]);
        Assert.AreEqual("parent", result.RootElement.GetProperty("comparison_parent").GetString());
    }

    [TestMethod]
    public void PrimaryRateLimitWaitsUntilResetWithSafetyMargin()
    {
        var now = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add(
            "X-RateLimit-Reset",
            now.AddMinutes(10).ToUnixTimeSeconds().ToString());

        var delay = GitHubApiClient.GetPrimaryRateLimitDelay(response, now);

        Assert.AreEqual(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(5), delay);
    }

    [TestMethod]
    public void TreeComparisonReconstructsAddedRemovedModifiedAndRenamedFiles()
    {
        var before = new Dictionary<string, (string Sha, string Mode)>(StringComparer.Ordinal)
        {
            ["removed.cs"] = ("removed", "100644"),
            ["old-name.cs"] = ("renamed", "100644"),
            ["changed.cs"] = ("old", "100644"),
            ["mode.sh"] = ("same", "100644")
        };
        var after = new Dictionary<string, (string Sha, string Mode)>(StringComparer.Ordinal)
        {
            ["added.cs"] = ("added", "100644"),
            ["new-name.cs"] = ("renamed", "100644"),
            ["changed.cs"] = ("new", "100644"),
            ["mode.sh"] = ("same", "100755")
        };

        var files = GitHubApiClient.ReconstructChangedFiles(before, after)
            .Select(raw => JsonDocument.Parse(raw))
            .ToArray();
        try
        {
            var byName = files.ToDictionary(
                document => document.RootElement.GetProperty("filename").GetString()!,
                document => document.RootElement);
            Assert.AreEqual(5, byName.Count);
            Assert.AreEqual("removed", byName["removed.cs"].GetProperty("status").GetString());
            Assert.AreEqual("added", byName["added.cs"].GetProperty("status").GetString());
            Assert.AreEqual("modified", byName["changed.cs"].GetProperty("status").GetString());
            Assert.AreEqual("modified", byName["mode.sh"].GetProperty("status").GetString());
            Assert.AreEqual("renamed", byName["new-name.cs"].GetProperty("status").GetString());
            Assert.AreEqual(
                "old-name.cs",
                byName["new-name.cs"].GetProperty("previous_filename").GetString());
        }
        finally
        {
            foreach (var file in files) file.Dispose();
        }
    }

    [TestMethod]
    public void ExceptionFormattingIncludesDatabaseCause()
    {
        var exception = new InvalidOperationException(
            "Salvataggio non riuscito.",
            new Exception("Violazione del vincolo univoco."));

        Assert.AreEqual(
            "Salvataggio non riuscito. | Causa: Violazione del vincolo univoco.",
            IngestionSyncTracker.FormatException(exception));
    }

    [TestMethod]
    public void PostgreSqlJsonNormalizationReplacesNullWithoutChangingLiteralEscape()
    {
        const string raw = "{\"invalid\":\"before\\u0000after\",\"literal\":\"before\\\\u0000after\"}";

        using var normalized = JsonDocument.Parse(PostgreSqlValueSanitizer.NormalizeJson(raw));

        Assert.AreEqual("before\uFFFDafter", normalized.RootElement.GetProperty("invalid").GetString());
        Assert.AreEqual("before\\u0000after", normalized.RootElement.GetProperty("literal").GetString());
    }

    private static GitHubApiClient CreateClient(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) =>
        new("test-token", new DelegateHandler(responder));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responder(request, Interlocked.Increment(ref callCount));
            return Task.FromResult(response);
        }
    }
}
