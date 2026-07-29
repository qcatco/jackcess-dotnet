using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Uniqueness on insert. A unique index — every primary key, plus a secondary index created with
/// <c>unique: true</c> — promises Access that no key repeats; Access enforces that for its own
/// writes and reads the index trusting it. Inserting a duplicate through this library used to
/// succeed and leave an index Access considers corrupt.
/// </summary>
public sealed class UniqueIndexTests : IDisposable
{
    private readonly string _path;
    public UniqueIndexTests()
        => _path = Path.Combine(Path.GetTempPath(), $"unique_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",    DataType.Long).Build(),
        new ColumnBuilder("Code",  DataType.Text).MaxLength(30).Build(),
        new ColumnBuilder("Label", DataType.Text).MaxLength(30).Build(),
    };

    private static Row RowFor(int i) => new()
    {
        ["Id"] = i, ["Code"] = $"code{i:D3}", ["Label"] = $"label{i}",
    };

    private void Seed(int rows, bool withUniqueCode = false)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("T", Columns(), primaryKey: "Id");
            for (int i = 0; i < rows; i++) t.Insert(RowFor(i));
        }
        if (withUniqueCode)
            using (var db = Database.Open(_path))
                db.CreateIndex("T", "UX_Code", unique: true, "Code");
    }

    [Fact]
    public void ADuplicatePrimaryKey_IsRefused()
    {
        Seed(10);

        using var db = Database.Open(_path);
        var table = db.GetTable("T");

        var ex = Assert.Throws<InvalidOperationException>(
            () => table.Insert(new Row { ["Id"] = 5, ["Code"] = "other", ["Label"] = "other" }));
        Assert.Contains("PrimaryKey", ex.Message);

        // Nothing was written: neither the row nor an index entry.
        Assert.Equal(10, db.GetTable("T").ReadAllRows().Count);
    }

    /// <summary>A table created in this session has no index metadata, only its primary-key tree.</summary>
    [Fact]
    public void ADuplicatePrimaryKey_IsRefusedOnAFreshlyCreatedTable()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", Columns(), primaryKey: "Id");
        for (int i = 0; i < 5; i++) table.Insert(RowFor(i));

        Assert.Throws<InvalidOperationException>(() => table.Insert(RowFor(3)));
        Assert.Equal(5, table.ReadAllRows().Count);
    }

    [Fact]
    public void ADuplicateInAUniqueSecondaryIndex_IsRefused()
    {
        Seed(10, withUniqueCode: true);

        using var db = Database.Open(_path);
        var table = db.GetTable("T");

        var ex = Assert.Throws<InvalidOperationException>(
            () => table.Insert(new Row { ["Id"] = 99, ["Code"] = "code005", ["Label"] = "clash" }));
        Assert.Contains("UX_Code", ex.Message);

        Assert.Equal(10, db.GetTable("T").ReadAllRows().Count);
    }

    [Fact]
    public void ANonUniqueIndex_StillTakesRepeatedValues()
    {
        Seed(10);

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "IX_Label", "Label");

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.Insert(new Row { ["Id"] = 100, ["Code"] = "c100", ["Label"] = "label1" });
            table.Insert(new Row { ["Id"] = 101, ["Code"] = "c101", ["Label"] = "label1" });
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(12, reopened.GetTable("T").ReadAllRows().Count);
    }

    /// <summary>
    /// A key with a null component is exempt, as SQL treats null as unequal to everything including
    /// itself. Access disagrees on this, so it is a documented divergence rather than an accident.
    /// </summary>
    [Fact]
    public void NullsAreExemptFromAUniqueSecondaryIndex()
    {
        Seed(3, withUniqueCode: true);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.Insert(new Row { ["Id"] = 50, ["Label"] = "no code" });
            table.Insert(new Row { ["Id"] = 51, ["Label"] = "also none" });
        }

        using var reopened = Database.Open(_path);
        Assert.Equal(5, reopened.GetTable("T").ReadAllRows().Count);
    }

    /// <summary>Deleting a key frees it, so the same value can be inserted again.</summary>
    [Fact]
    public void AfterDeletingARow_ItsKeyCanBeUsedAgain()
    {
        Seed(10, withUniqueCode: true);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            table.DeleteRow("Id", 5);
            table.Insert(new Row { ["Id"] = 5, ["Code"] = "code005", ["Label"] = "reused" });
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("T");

        Assert.Equal(10, reread.ReadAllRows().Count);
        Row? found = reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(5);
        Assert.NotNull(found);
        Assert.Equal("reused", found!["Label"]);
    }
}
