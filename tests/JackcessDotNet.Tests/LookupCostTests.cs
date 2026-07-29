using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Deleting and updating used to find their row by reading every data page in the table — twice
/// over, once in <c>Table</c> and once again in the writer. Both now seek through a single-column
/// index when one covers the column, so the cost stops growing with the table.
/// <para>
/// The assertions are on <see cref="PageFile.PagesRead"/>, since page reads are what the work
/// actually costs and correctness tests cannot tell a seek from a scan.
/// </para>
/// </summary>
public sealed class LookupCostTests : IDisposable
{
    private const int Rows = 4000;

    private readonly string _path;
    public LookupCostTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cost_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>Rows wide enough that 4000 of them span a few hundred data pages.</summary>
    private static Row RowFor(int i) => new()
    {
        ["Id"] = i, ["Name"] = $"name{i:D5}", ["Filler"] = new string('f', 120),
    };

    private void Seed()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Id",     DataType.Long).Build(),
            new ColumnBuilder("Name",   DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Filler", DataType.Text).MaxLength(150).Build(),
        }, primaryKey: "Id");
        for (int i = 0; i < Rows; i++) t.Insert(RowFor(i));
    }

    /// <summary>Pages the table's own data occupies — the floor a scan cannot beat.</summary>
    private static int DataPageCount(Table table)
        => table.ReadAllRows().Count == 0 ? 0 : DistinctPages(table);

    private static int DistinctPages(Table table)
    {
        var pages = new HashSet<int>();
        foreach (var (_, rowPtr) in table.EnumerateRowsWithPointers())
            pages.Add((rowPtr >> 16) & 0xFFFFFF);
        return pages.Count;
    }

    [Fact]
    public void DeletingByAnIndexedColumn_DoesNotReadTheWholeTable()
    {
        Seed();

        using var db = Database.Open(_path);
        var table = db.GetTable("T");
        int dataPages = DataPageCount(table);
        Assert.True(dataPages > 50, $"the fixture needs to span many pages to be meaningful; spans {dataPages}");

        var file = db.File;
        long before = file.PagesRead;
        table.DeleteRow("Id", Rows / 2);
        long cost = file.PagesRead - before;

        // A scan reads every data page at least once, and the old code did that twice over. An
        // index seek is a handful of pages: the tree, the row, and the pages it rewrites.
        Assert.True(cost < dataPages / 2,
            $"deleting one row read {cost} pages; the table spans {dataPages} data pages, so this looks like a scan");
    }

    [Fact]
    public void UpdatingByPrimaryKey_DoesNotReadTheWholeTable()
    {
        Seed();

        using var db = Database.Open(_path);
        var table = db.GetTable("T");
        int dataPages = DataPageCount(table);

        var file = db.File;
        long before = file.PagesRead;
        table.UpdateByPrimaryKey(Rows / 2, new Row { ["Name"] = "renamed" });
        long cost = file.PagesRead - before;

        Assert.True(cost < dataPages / 2,
            $"updating one row read {cost} pages; the table spans {dataPages} data pages, so this looks like a scan");
    }

    /// <summary>
    /// Loading rows used to read every page the table already had, on every single insert, so the
    /// cost of a bulk load grew with the square of its size. It should now be roughly linear.
    /// </summary>
    [Fact]
    public void BulkInsert_CostsRoughlyLinearPageReads()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("T", new[]
        {
            new ColumnBuilder("Id",     DataType.Long).Build(),
            new ColumnBuilder("Filler", DataType.Text).MaxLength(150).Build(),
        }, primaryKey: "Id");

        var file = db.File;
        long before = file.PagesRead;
        for (int i = 0; i < Rows; i++)
            t.Insert(new Row { ["Id"] = i, ["Filler"] = new string('f', 120) });
        long cost = file.PagesRead - before;

        // Quadratic would be ~4000 × ~140 pages ≈ 560,000 reads by the end. A generous linear
        // bound still separates the two by an order of magnitude.
        Assert.True(cost < Rows * 20L,
            $"inserting {Rows} rows read {cost} pages, which looks quadratic rather than linear");
    }

    /// <summary>
    /// Deleting every row on a page should hand that page back for reuse, so a churning file stops
    /// growing. Space inside a page that still holds live rows is a different matter — that needs
    /// compaction, which is not done.
    /// </summary>
    [Fact]
    public void DeletingEveryRowOnAPage_LetsTheFileStopGrowing()
    {
        Seed();

        long afterSeed;
        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            afterSeed = new FileInfo(_path).Length;

            // Delete the lot, then write as many rows again. With pages reclaimed this reuses them.
            for (int i = 0; i < Rows; i++) t.DeleteRow("Id", i);
            for (int i = 0; i < Rows; i++) t.Insert(RowFor(i + Rows));
        }

        long afterChurn = new FileInfo(_path).Length;

        // Without reclamation the second pass allocates a fresh page for every row it writes, so the
        // file would roughly double.
        Assert.True(afterChurn < afterSeed * 3 / 2,
            $"file grew from {afterSeed} to {afterChurn} bytes over a delete-all/rewrite cycle, "
          + "so emptied pages are not being reused");
    }

    /// <summary>Cheap is worthless if it is wrong, so the same operations are checked for effect.</summary>
    [Fact]
    public void TheCheapPathStillDeletesAndUpdatesTheRightRow()
    {
        Seed();

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            t.DeleteRow("Id", 10);
            t.UpdateByPrimaryKey(20, new Row { ["Name"] = "renamed" });
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("T");

        Assert.Equal(Rows - 1, table.ReadAllRows().Count);
        Assert.Null(table.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(10));

        Row? updated = table.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(20);
        Assert.NotNull(updated);
        Assert.Equal("renamed", updated!["Name"]);

        Row? untouched = table.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(21);
        Assert.NotNull(untouched);
        Assert.Equal("name00021", untouched!["Name"]);
    }
}
