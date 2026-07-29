using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Adding a secondary index to a table that already exists. The index sections are spliced into
/// the table's already-written TDEF page — a new row-count block before the columns, a column
/// block and a slot after them, and a name — and the tree is then filled from the rows already
/// in the table.
/// </summary>
public sealed class CreateIndexTests : IDisposable
{
    private readonly string _path;
    public CreateIndexTests()
        => _path = Path.Combine(Path.GetTempPath(), $"mkidx_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        new ColumnBuilder("City", DataType.Text).MaxLength(50).Build(),
    };

    private static Row RowFor(int i) => new()
    {
        ["Id"] = i, ["Name"] = $"name{i:D4}", ["City"] = $"city{i % 7}",
    };

    /// <summary>Creates the table with <paramref name="rows"/> rows already in it.</summary>
    private void SeedTable(int rows)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        for (int i = 0; i < rows; i++) table.Insert(RowFor(i));
    }

    [Fact]
    public void ANewIndex_IsReadBackWithItsOwnDataBlockAndColumns()
    {
        SeedTable(50);

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "ByName", "Name");

        using var reopened = Database.Open(_path);
        var indexes = reopened.GetTable("T").Indexes;

        var byName = Assert.Single(indexes, i => i.Name == "ByName");
        var pk     = Assert.Single(indexes, i => i.IsPrimaryKey);

        Assert.Equal("Name", Assert.Single(byName.Columns).Column.Name);
        Assert.True(byName.Columns[0].IsAscending);
        Assert.False(byName.IsPrimaryKey);
        Assert.True(byName.RootPageNumber > 0);

        // Its own tree and its own block — the two must not collide.
        Assert.NotEqual(pk.RootPageNumber, byName.RootPageNumber);
        Assert.NotEqual(pk.IndexDataNumber, byName.IndexDataNumber);
    }

    [Fact]
    public void ANewIndex_IsBackfilledFromTheRowsAlreadyInTheTable()
    {
        const int Rows = 50;
        SeedTable(Rows);

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "ByName", "Name");

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("T");

        for (int i = 0; i < Rows; i++)
        {
            Row? found = table.NewIndexCursor("ByName").FindRow("Name", $"name{i:D4}");
            Assert.NotNull(found);
            Assert.Equal(i, found!["Id"]);
        }
    }

    [Fact]
    public void RowsInsertedAfterwards_AreThreadedIntoTheNewIndexToo()
    {
        SeedTable(10);

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "ByName", "Name");

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            for (int i = 10; i < 30; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("T");

        Assert.Equal(30, reread.ReadAllRows().Count);
        for (int i = 0; i < 30; i++)
        {
            Assert.NotNull(reread.NewIndexCursor("ByName").FindRow("Name", $"name{i:D4}"));
            Assert.NotNull(reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i));
        }
    }

    [Fact]
    public void ACompositeIndex_SpansItsColumnsInOrder()
    {
        SeedTable(20);

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "ByCityName", "City", "Name");

        using var reopened = Database.Open(_path);
        var ix = Assert.Single(reopened.GetTable("T").Indexes, i => i.Name == "ByCityName");

        Assert.Equal(new[] { "City", "Name" }, ix.Columns.Select(c => c.Column.Name));
    }

    [Fact]
    public void CreateIndex_RejectsADuplicateNameAndAnUnknownColumn()
    {
        SeedTable(5);

        using var db = Database.Open(_path);
        Assert.Throws<InvalidOperationException>(() => db.CreateIndex("T", "PrimaryKey", "Name"));
        Assert.Throws<InvalidOperationException>(() => db.CreateIndex("T", "ByNope", "Nope"));
    }
}
