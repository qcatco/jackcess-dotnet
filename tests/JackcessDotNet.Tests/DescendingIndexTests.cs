using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Indexes with a descending column. Jet stores such a column's key bytes inverted — the one's
/// complement of its ascending form — so a single byte-wise comparison walks it backwards, and the
/// column's flag byte in the TDEF has its ascending bit clear.
/// <para>
/// The writer used to emit the ascending flag and ascending bytes for every column, so a descending
/// index simply could not be created. Writing the flag without inverting the bytes would have been
/// worse than not supporting it: Access would read the flag, expect inverted keys, and seek the
/// wrong way through keys that were not.
/// </para>
/// </summary>
public sealed class DescendingIndexTests : IDisposable
{
    private readonly string _path;
    public DescendingIndexTests()
        => _path = Path.Combine(Path.GetTempPath(), $"descidx_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private const int Rows = 40;

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",    DataType.Long).Build(),
        new ColumnBuilder("Score", DataType.Long).Build(),
        new ColumnBuilder("Name",  DataType.Text).MaxLength(40).Build(),
    };

    private static Row RowFor(int i) => new()
    {
        ["Id"] = i, ["Score"] = 1000 - i, ["Name"] = $"name{i:D3}",
    };

    private void Seed()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("T", Columns(), primaryKey: "Id");
        for (int i = 0; i < Rows; i++) t.Insert(RowFor(i));
    }

    [Fact]
    public void ADescendingIndex_IsReadBackAsDescending()
    {
        Seed();

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_ScoreDesc", unique: false, new IndexColumnSpec("Score", Ascending: false));

        using var reopened = Database.Open(_path);
        var ix = Assert.Single(reopened.GetTable("T").Indexes, i => i.Name == "IX_ScoreDesc");

        var col = Assert.Single(ix.Columns);
        Assert.Equal("Score", col.Column.Name);
        Assert.False(col.IsAscending);
    }

    [Fact]
    public void ADescendingIndex_FindsEveryRowItIndexes()
    {
        Seed();

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_ScoreDesc", unique: false, new IndexColumnSpec("Score", Ascending: false));

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("T");

        for (int i = 0; i < Rows; i++)
        {
            Row? found = table.NewIndexCursor("IX_ScoreDesc").FindRow("Score", 1000 - i);
            Assert.NotNull(found);
            Assert.Equal(i, found!["Id"]);
        }
    }

    [Fact]
    public void RowsAddedAfterwards_LandInTheDescendingIndex()
    {
        Seed();

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_ScoreDesc", unique: false, new IndexColumnSpec("Score", Ascending: false));

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            for (int i = Rows; i < Rows + 20; i++) t.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("T");

        for (int i = Rows; i < Rows + 20; i++)
            Assert.NotNull(table.NewIndexCursor("IX_ScoreDesc").FindRow("Score", 1000 - i));
    }

    /// <summary>A text column collates in reverse rather than being complemented after the fact.</summary>
    [Fact]
    public void ADescendingTextIndex_FindsEveryRowItIndexes()
    {
        Seed();

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_NameDesc", unique: false, new IndexColumnSpec("Name", Ascending: false));

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("T");

        for (int i = 0; i < Rows; i++)
        {
            Row? found = table.NewIndexCursor("IX_NameDesc").FindRow("Name", $"name{i:D3}");
            Assert.NotNull(found);
            Assert.Equal(i, found!["Id"]);
        }
    }

    /// <summary>Mixed directions in one composite key, which is why the flag is per column.</summary>
    [Fact]
    public void ACompositeIndex_CanMixDirections()
    {
        Seed();

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_Mixed", unique: false,
                           new IndexColumnSpec("Name"),
                           new IndexColumnSpec("Score", Ascending: false));

        using var reopened = Database.Open(_path);
        var ix = Assert.Single(reopened.GetTable("T").Indexes, i => i.Name == "IX_Mixed");

        Assert.Equal(2, ix.Columns.Count);
        Assert.True(ix.Columns[0].IsAscending);
        Assert.False(ix.Columns[1].IsAscending);
    }

    /// <summary>A plain column name still means ascending, so existing calls are unaffected.</summary>
    [Fact]
    public void APlainColumnNameStillMeansAscending()
    {
        Seed();

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_Score", "Score");

        using var reopened = Database.Open(_path);
        var ix = Assert.Single(reopened.GetTable("T").Indexes, i => i.Name == "IX_Score");

        Assert.True(Assert.Single(ix.Columns).IsAscending);
    }
}
