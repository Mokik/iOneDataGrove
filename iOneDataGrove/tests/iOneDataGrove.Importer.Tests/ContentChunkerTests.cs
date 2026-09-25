using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iOneDataGrove.Importer.Tests;

[TestClass]
public sealed class ContentChunkerTests
{
    [TestMethod]
    public void LineChunksKeepStableLineRangesAndOverlap()
    {
        var content = string.Join(
            "\n",
            Enumerable.Range(1, 250).Select(line => $"riga {line:000}"));
        var chunks = ContentChunker.SplitLines(
            content,
            baseStartLine: 20,
            maxLines: 100,
            overlapLines: 10,
            maxCharacters: 20_000);

        Assert.HasCount(3, chunks);
        Assert.AreEqual((20, 119), (chunks[0].StartLine, chunks[0].EndLine));
        Assert.AreEqual((110, 209), (chunks[1].StartLine, chunks[1].EndLine));
        Assert.AreEqual((200, 269), (chunks[2].StartLine, chunks[2].EndLine));
        StringAssert.Contains(chunks[0].Content, "riga 100");
        StringAssert.Contains(chunks[1].Content, "riga 091");
        StringAssert.Contains(chunks[2].Content, "riga 250");
    }

    [TestMethod]
    public void TextChunksRespectMaximumSizeAndKeepContextOverlap()
    {
        var content = string.Join(
            " ",
            Enumerable.Range(1, 1_500).Select(index => $"termine{index:0000}"));
        var chunks = ContentChunker.SplitText(
            content,
            maxCharacters: 1_000,
            overlapCharacters: 100);

        Assert.IsGreaterThan(10, chunks.Count);
        Assert.IsTrue(chunks.All(chunk => chunk.Content.Length <= 1_000));
        for (var index = 1; index < chunks.Count; index++)
        {
            var previousWords = chunks[index - 1].Content.Split(' ');
            var currentWords = chunks[index].Content.Split(' ');
            Assert.IsTrue(
                previousWords.TakeLast(3).Intersect(currentWords.Take(12)).Any(),
                $"I chunk {index - 1} e {index} devono condividere il contesto.");
        }
    }

    [TestMethod]
    public void EmptyContentProducesNoChunks()
    {
        Assert.IsEmpty(ContentChunker.SplitText("  \r\n "));
        Assert.IsEmpty(ContentChunker.SplitLines(null));
    }

    [TestMethod]
    public void SourceHashIsDeterministicAndChangesWithContent()
    {
        var first = ContentChunkIndexer.ComputeHash("issue", 42, "contenuto");
        var same = ContentChunkIndexer.ComputeHash("issue", 42, "contenuto");
        var changed = ContentChunkIndexer.ComputeHash("issue", 42, "contenuto aggiornato");

        Assert.AreEqual(first, same);
        Assert.AreNotEqual(first, changed);
        Assert.AreEqual(64, first.Length);
    }
}
