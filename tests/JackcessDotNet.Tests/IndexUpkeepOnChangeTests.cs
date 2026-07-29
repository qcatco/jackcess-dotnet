using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Index upkeep when a row is deleted or updated, not just inserted. Access reaches rows through
/// its indexes, so an entry left behind for a deleted row is a pointer to nothing, and it also
/// makes Access over-report: it answers <c>COUNT(*)</c> from an index rather than by scanning.
/// </summary>
public sealed class IndexUpkeepOnChangeTests : IDisposable
{
    private readonly string _path;
    public IndexUpkeepOnChangeTests()
        => _path = Path.Combine(Path.GetTempPath(), $"upkeep_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        new ColumnBuilder("City", DataType.Text).MaxLength(50).Build(),
    };

    private static Row RowFor(int i) => new()
    {
        ["Id"] = i, ["Name"] = $"name{i:D4}", ["City"] = $"city{i % 5}",
    };

    /// <summary>Seeds a table with a primary key and a secondary index, then closes the file.</summary>
    private void Seed(int rows)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("T", Columns(), primaryKey: "Id");
            for (int i = 0; i < rows; i++) t.Insert(RowFor(i));
        }
        using (var db = Database.Open(_path))
            db.CreateIndex("T", "ByName", "Name");
    }


    // ── On-disk assertions ────────────────────────────────────────────────────
    // This library's IndexCursor filters entries whose row no longer matches, so a stale entry
    // left by a delete is invisible to it and every seek-based assertion passes regardless.
    // Access does no such filtering: it follows the entry to a deleted slot, and answers COUNT(*)
    // from the index's stored entry count. So the defect has to be measured on the page.

    /// <summary>Total entries across every leaf of an index, following the leaf chain.</summary>
    private int LeafEntryCount(string indexName)
    {
        using var db = Database.Open(_path);
        var ix = db.GetTable("T").Indexes.Single(
            i => i.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase));

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        var format = JetFormat.Jet4;

        // Descend the leftmost spine, then walk `next` to the end.
        int page = ix.RootPageNumber;
        while (pf.ReadPage(page)[0] == JetFormat.PageTypeIndexNode)
        {
            byte[] node = pf.ReadPage(page);
            int first = -1, maskPos = format.OffsetIndexEntryMask, maskLen = format.SizeIndexEntryMask;
            int entriesPos = maskPos + maskLen, lastStart = 0;
            for (int i = 0; i < maskLen && first < 0; i++)
                for (int j = 0; j < 8 && first < 0; j++)
                {
                    if ((node[maskPos + i] & (1 << j)) == 0) continue;
                    int end = i * 8 + j, len = end - lastStart, abs = entriesPos + lastStart;
                    if (len >= 8)
                    {
                        int k = abs + len - 4;
                        first = (node[k] << 24) | (node[k + 1] << 16) | (node[k + 2] << 8) | node[k + 3];
                    }
                    lastStart = end;
                }
            page = first > 0 ? first : ByteUtil.GetInt(node, format.OffsetChildTailIndexPage);
        }

        int total = 0;
        var seen = new HashSet<int>();
        while (page > 0 && seen.Add(page))
        {
            byte[] leaf = pf.ReadPage(page);
            for (int i = 0; i < format.SizeIndexEntryMask; i++)
                for (int j = 0; j < 8; j++)
                    if ((leaf[format.OffsetIndexEntryMask + i] & (1 << j)) != 0) total++;
            page = ByteUtil.GetInt(leaf, format.OffsetNextIndexPage);
        }
        return total;
    }

    /// <summary>The entry count Access reads out of the index's row-count block in the TDEF.</summary>
    private int StoredRowCount(string indexName)
    {
        using var db = Database.Open(_path);
        var table = db.GetTable("T");
        var ix = table.Indexes.Single(i => i.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase));

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] tdef = pf.ReadPage(table.Definition.TdefPageNumber);
        var format = JetFormat.Jet4;
        return ByteUtil.GetInt(tdef, format.SizeTdefHeader + ix.IndexDataNumber * format.SizeIndexDefinition + 4);
    }

    [Fact]
    public void DeletingARow_RemovesItFromThePrimaryKeyIndex()
    {
        Seed(40);

        using (var db = Database.Open(_path))
            db.GetTable("T").DeleteRow("Id", 17);

        using var reopened = Database.Open(_path);
        var t = reopened.GetTable("T");

        Assert.Equal(39, t.ReadAllRows().Count);
        Assert.Null(t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(17));

        // Its neighbours must be untouched.
        Assert.NotNull(t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(16));
        Assert.NotNull(t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(18));

        reopened.Dispose();
        Assert.Equal(39, LeafEntryCount("PrimaryKey"));
        Assert.Equal(39, StoredRowCount("PrimaryKey"));
    }

    [Fact]
    public void DeletingARow_RemovesItFromEverySecondaryIndexToo()
    {
        Seed(40);

        using (var db = Database.Open(_path))
            db.GetTable("T").DeleteRow("Id", 17);

        using var reopened = Database.Open(_path);
        var t = reopened.GetTable("T");

        Assert.Null(t.NewIndexCursor("ByName").FindRow("Name", "name0017"));
        Assert.NotNull(t.NewIndexCursor("ByName").FindRow("Name", "name0016"));
        Assert.NotNull(t.NewIndexCursor("ByName").FindRow("Name", "name0018"));

        reopened.Dispose();
        Assert.Equal(39, LeafEntryCount("ByName"));
        Assert.Equal(39, StoredRowCount("ByName"));
    }

    /// <summary>
    /// Deleting the greatest key on a leaf leaves its parent claiming a range the leaf no longer
    /// covers, and a search inside that gap then descends into the wrong leaf and finds nothing.
    /// Enough rows here that the tree has several leaves.
    /// </summary>
    [Fact]
    public void DeletingTheGreatestKeyOnALeaf_DoesNotHideItsNeighbours()
    {
        const int Rows = 600;
        Seed(Rows);

        // Delete a run, which is bound to take some leaf's maximum with it.
        using (var db = Database.Open(_path))
        {
            var target = db.GetTable("T");
            for (int i = 100; i < 140; i++) target.DeleteRow("Id", i);
        }

        using var reopened = Database.Open(_path);
        var t = reopened.GetTable("T");

        Assert.Equal(Rows - 40, t.ReadAllRows().Count);

        for (int i = 100; i < 140; i++)
            Assert.Null(t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i));

        // Everything else must still be reachable — this is what a stale parent key breaks.
        for (int i = 0; i < Rows; i++)
        {
            if (i >= 100 && i < 140) continue;
            Assert.NotNull(t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i));
        }

        reopened.Dispose();
        Assert.Equal(Rows - 40, LeafEntryCount("PrimaryKey"));
        Assert.Equal(Rows - 40, StoredRowCount("PrimaryKey"));
    }

    [Fact]
    public void UpdatingAnIndexedValue_MovesTheEntryInsteadOfLeavingTwo()
    {
        Seed(40);

        using (var db = Database.Open(_path))
            db.GetTable("T").UpdateByPrimaryKey(17, new Row { ["Name"] = "renamed" });

        using var reopened = Database.Open(_path);
        var t = reopened.GetTable("T");

        Assert.Equal(40, t.ReadAllRows().Count);

        // The new value is indexed and the old one is gone.
        Row? renamed = t.NewIndexCursor("ByName").FindRow("Name", "renamed");
        Assert.NotNull(renamed);
        Assert.Equal(17, renamed!["Id"]);
        Assert.Null(t.NewIndexCursor("ByName").FindRow("Name", "name0017"));

        // The primary key still finds the row, at its new location.
        Row? byPk = t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(17);
        Assert.NotNull(byPk);
        Assert.Equal("renamed", byPk!["Name"]);

        // One entry per row per index — an update must move entries, not add a second set.
        reopened.Dispose();
        Assert.Equal(40, LeafEntryCount("PrimaryKey"));
        Assert.Equal(40, LeafEntryCount("ByName"));
        Assert.Equal(40, StoredRowCount("PrimaryKey"));
        Assert.Equal(40, StoredRowCount("ByName"));
    }

    [Fact]
    public void DeletingThenReinsertingTheSameKey_FindsTheNewRow()
    {
        Seed(40);

        using (var db = Database.Open(_path))
        {
            var target = db.GetTable("T");
            target.DeleteRow("Id", 17);
            target.Insert(new Row { ["Id"] = 17, ["Name"] = "again", ["City"] = "cityX" });
        }

        using var reopened = Database.Open(_path);
        var t = reopened.GetTable("T");

        Row? found = t.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(17);
        Assert.NotNull(found);
        Assert.Equal("again", found!["Name"]);
    }
}
