using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table's primary key holds an entry for each of its rows and no other, as Access keeps one: a deleted row's entry
/// goes with it, an updated row's entry moves with it, and the tree keeps Access's rules as pages empty. Access counts
/// and finds a table's rows through its key - an entry left behind by a delete was a row ACE counted that was not there.
/// </summary>
public sealed class PrimaryKeyMaintenanceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pk_upkeep_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Column[] Columns() => new[]
    {
        new ColumnBuilder("Id", DataType.Long).Build(),
        new ColumnBuilder("Txt", DataType.Text).MaxLength(30).Build(),
    };

    // Keys 1..count, inserted out of order.
    private static Table Keyed(Database db, int count)
    {
        var table = db.CreateTable("Keyed", Columns(), primaryKey: "Id");
        foreach (int key in Enumerable.Range(1, count).OrderBy(k => (k * 7919L) % 10007))
            table.Insert(new Row { ["Id"] = key, ["Txt"] = $"v{key}" });
        return table;
    }

    private static int TdefOf(Database db) => (int)JetPages.Rows(db.File, JetFormat.PageSystemCatalog)
        .Single(r => r.Values.TryGetValue("Name", out var name) && (string?)name == "Keyed").Values["Id"]!;

    // The key's entries are the table's rows, one each, and its tree keeps Access's rules.
    private static void AssertKeyHoldsTheRows(Database db, int expectedRows)
    {
        var file = db.File;
        int tdef = TdefOf(db);
        var rows = JetPages.Rows(file, tdef);
        Assert.Equal(expectedRows, rows.Count);

        var block = JetPages.IndexBlocks(file, tdef).Single();
        var entries = JetPages.LeafEntries(file, block.Root);
        Assert.Equal(rows.Select(r => (r.Page, r.Row)).OrderBy(p => p), entries.Select(e => (e.RowPage, e.Row)).OrderBy(p => p));
        var byPointer = rows.ToDictionary(r => (r.Page, r.Row), r => (int)r.Values["Id"]!);
        foreach (var e in entries)
            Assert.Equal(Convert.ToHexString(IndexKeys.EncodeColumn(DataType.Long, true, byPointer[(e.RowPage, e.Row)])), e.Hex);

        Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        // The index's usage map lists exactly its tree's pages: those a split added, less those that emptied.
        var tree = JetPages.TreePages(file, block.Root).Select(p => p.Number).OrderBy(p => p);
        Assert.Equal(tree, UsageMap.GetOwnedPages(file.ReadPage(block.UmapPage), block.UmapRow, file.Format, file).OrderBy(p => p));
    }

    [Fact]
    public void Deleted_rows_take_their_entries_out_of_the_primary_key()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Keyed(db, 2000);
        int pagesBefore = JetPages.TreePages(db.File, JetPages.IndexBlocks(db.File, TdefOf(db)).Single().Root).Count;

        for (int key = 201; key <= 1800; key++)   // the middle: whole leaves empty
            table.DeleteRow("Id", key);

        AssertKeyHoldsTheRows(db, 400);
        Assert.True(JetPages.TreePages(db.File, JetPages.IndexBlocks(db.File, TdefOf(db)).Single().Root).Count < pagesBefore);
        var cursor = db.GetTable("Keyed").NewIndexCursor();
        Assert.Equal("v200", cursor.FindRowByPrimaryKey(200)?["Txt"]);
        Assert.Null(cursor.FindRowByPrimaryKey(1000));
        Assert.Equal("v1801", cursor.FindRowByPrimaryKey(1801)?["Txt"]);
    }

    [Fact]
    public void Emptying_the_last_leaf_hands_its_place_to_the_leaf_before()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Keyed(db, 2000);

        for (int key = 1001; key <= 2000; key++)   // the top: the tail empties
            table.DeleteRow("Id", key);
        AssertKeyHoldsTheRows(db, 1000);

        for (int key = 2001; key <= 2100; key++)    // keys after the old tail's go in at the end again
            table.Insert(new Row { ["Id"] = key, ["Txt"] = $"v{key}" });
        AssertKeyHoldsTheRows(db, 1100);
        Assert.Equal("v2050", db.GetTable("Keyed").NewIndexCursor().FindRowByPrimaryKey(2050)?["Txt"]);
    }

    [Fact]
    public void Deleting_every_row_empties_the_primary_key_and_it_fills_again()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Keyed(db, 1500);
        for (int key = 1; key <= 1500; key++)
            table.DeleteRow("Id", key);

        AssertKeyHoldsTheRows(db, 0);
        Assert.True(JetPages.ReadIndexPage(db.File, JetPages.IndexBlocks(db.File, TdefOf(db)).Single().Root).IsLeaf);

        for (int key = 1; key <= 600; key++)
            table.Insert(new Row { ["Id"] = key, ["Txt"] = $"again {key}" });
        AssertKeyHoldsTheRows(db, 600);
    }

    [Fact]
    public void An_updated_row_moves_its_primary_key_entry()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = Keyed(db, 10);

        table.UpdateByPrimaryKey(3, new Row { ["Txt"] = "updated" });

        AssertKeyHoldsTheRows(db, 10);
        Assert.Equal("updated", db.GetTable("Keyed").NewIndexCursor().FindRowByPrimaryKey(3)?["Txt"]);
    }
}
