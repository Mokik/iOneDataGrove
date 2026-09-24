using iOneDataGrove.Importer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iOneDataGrove.Importer.Tests;

[TestClass]
public sealed class CommitRecoveryTests
{
    private const string Sha = "8d98f4036ba9534de7210af9af264d3cec0d5d4f";
    private const string Payload = """
        {"sha":"8d98f4036ba9534de7210af9af264d3cec0d5d4f",
         "commit":{"message":"#567 fix: aggiornata condizione filtro CanaleDiVenditaCodice",
           "author":{"name":"Example","date":"2026-09-03T13:42:16+02:00"},"committer":null},
         "stats":{"additions":1,"deletions":1},
         "files":[{"filename":"IOne.Domain/Helpers/PrefatturazioneHelper.cs",
           "status":"modified","additions":1,"deletions":1,"changes":2,"patch":"@@ -1 +1 @@"}]}
        """;

    [TestMethod]
    public void Issue567IsAReferenceNotAnAutomaticClosure()
    {
        var commit = GitHubCommitRecovery.ParseCommit(Payload, Sha);
        var references = KnowledgeLinkIndexer.ExtractReferences(commit.Message, "iOneSolutionsSrl/iOneGavio");
        Assert.HasCount(1, references);
        Assert.AreEqual(567, references[0].Number);
        Assert.AreEqual("references", references[0].RelationType);
        Assert.AreEqual("IOne.Domain/Helpers/PrefatturazioneHelper.cs", commit.CommitFiles.Single().Filename);
        Assert.AreEqual(1, commit.FilesChanged);
        Assert.AreEqual(new DateTime(2026, 9, 3, 11, 42, 16, DateTimeKind.Utc), commit.AuthoredAt);
        Assert.IsNull(commit.CommittedAt);
    }

    [TestMethod]
    public void RecoveryRejectsOtherCommitAndNonExactSha()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GitHubCommitRecovery.ValidateSha("main"));
        Assert.ThrowsExactly<ArgumentException>(() => GitHubCommitRecovery.ValidateSha("8d98f403"));
        Assert.ThrowsExactly<InvalidOperationException>(() => GitHubCommitRecovery.ParseCommit(Payload, new string('a', 40)));
    }
}
