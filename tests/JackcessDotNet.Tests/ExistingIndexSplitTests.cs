using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Inserting into an Access-authored table until one of its index leaves splits.
/// <para>
/// The fixture is <c>common1V2000.mdb</c>'s <c>Table1</c>, chosen because Access wrote its two
/// indexes with the slots and the index-data blocks in <em>opposite</em> order — slot 0 is the
/// secondary index <c>B</c> but its tree lives in data block 1, while <c>PrimaryKey</c> is slot 1
/// and owns block 0. Any per-index field addressed by the slot position therefore lands in the
/// other index's block, which is invisible until a split makes the writer record a new root.
/// </para>
/// </summary>
public sealed class ExistingIndexSplitTests : IDisposable
{
    private const string Fixture = "common1V2000.mdb";
    private const string TableName = "Table1";

    private readonly string _path;

    public ExistingIndexSplitTests()
        => _path = Path.Combine(Path.GetTempPath(), $"split_{Guid.NewGuid():N}.mdb");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>Copies the fixture out of the read-only corpus; false when no corpus is present.</summary>
    private bool TryStageFixture()
    {
        string? corpus = CorpusPath.Resolve();
        if (corpus is null) return false;
        string source = Path.Combine(corpus, "V2000", Fixture);
        if (!File.Exists(source)) return false;
        File.Copy(source, _path, overwrite: true);
        return true;
    }

    /// <summary>Long values so a 2 KB leaf fills within a few dozen inserts.</summary>
    private static Row RowFor(int i) => new()
    {
        ["A"] = $"A{i:D4}-{new string('a', 30)}",
        ["B"] = $"B{i:D4}-{new string('b', 30)}",
    };

    [Fact]
    public void AfterALeafSplits_BothIndexesStillFindEveryRowTheyIndex()
    {
        if (!TryStageFixture()) return;

        const int Rows = 120;

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(TableName);
            for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));
        }

        // Reopen so every assertion reads the indexes back off the patched TDEF.
        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable(TableName);

        Assert.Equal(2 + Rows, reread.ReadAllRows().Count);

        for (int i = 0; i < Rows; i++)
        {
            var expected = RowFor(i);

            Row? byPk = reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(expected["A"]!);
            Assert.NotNull(byPk);
            Assert.Equal(expected["B"], byPk!["B"]);

            Row? byB = reread.NewIndexCursor("B").FindRow("B", expected["B"]!);
            Assert.NotNull(byB);
            Assert.Equal(expected["A"], byB!["A"]);
        }
    }

    /// <summary>
    /// The two indexes must keep separate trees. Patching a new root into the wrong block makes
    /// them converge on one page, which the seek test above only catches indirectly.
    /// </summary>
    [Fact]
    public void AfterALeafSplits_TheTwoIndexesStillHaveDistinctRoots()
    {
        if (!TryStageFixture()) return;

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(TableName);
            for (int i = 0; i < 120; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var indexes = reopened.GetTable(TableName).Indexes;

        var pk = indexes.Single(i => i.Name == "PrimaryKey");
        var b  = indexes.Single(i => i.Name == "B");

        Assert.NotEqual(pk.RootPageNumber, b.RootPageNumber);
        Assert.Equal(0, pk.IndexDataNumber);
        Assert.Equal(1, b.IndexDataNumber);
    }
}
