using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Appending values to a complex column with <see cref="Table.AddComplexValue"/>. The values live
/// in a per-column flat table linked to the owning row by its complex id, so writing one means
/// filling both link columns: the foreign key back to the row, and the flat row's own sequential
/// id, which Access numbers across the whole flat table rather than per owning row.
/// <para>
/// These run against an <c>.accdb</c>, which is the only place complex columns exist, and are the
/// first write tests in this suite that do — everything else writes Jet 4 <c>.mdb</c>. That gap is
/// how the encoder came to disagree with the reader about where a value lives: Access stores these
/// flat tables' Long foreign keys in the variable-length area, the reader was taught to follow the
/// column's flag, and the writer went on placing them at a fixed offset.
/// </para>
/// </summary>
public sealed class ComplexColumnWriteTests : IDisposable
{
    private readonly string _path;
    public ComplexColumnWriteTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cplxwrite_{Guid.NewGuid():N}.accdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private bool TryStage()
    {
        string? corpus = CorpusPath.Resolve();
        if (corpus is null) return false;
        string source = Path.Combine(corpus, "V2007", "complexDataV2007.accdb");
        if (!File.Exists(source)) return false;
        File.Copy(source, _path, overwrite: true);
        return true;
    }

    private static Row RowWithId(Table table, string id)
        => table.ReadAllRows().Single(r => (string)r["id"]! == id);

    [Fact]
    public void APlainRowCanBeInsertedIntoAnAccdb()
    {
        if (!TryStage()) return;

        using (var db = Database.Open(_path))
            db.GetTable("Table1").Insert(new Row { ["id"] = "written" });

        using var reopened = Database.Open(_path);
        Assert.Equal(5, reopened.GetTable("Table1").ReadAllRows().Count);
    }

    [Fact]
    public void AValueAddedToAMultiValueField_IsReadBack()
    {
        if (!TryStage()) return;

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("Table1");
            // row1 starts with none, so the new value cannot be confused with an existing one.
            t.AddComplexValue(RowWithId(t, "row1"), "multi-value-data", new Row { ["Value"] = "added-by-test" });
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("Table1");

        Assert.Equal(
            new[] { "added-by-test" },
            table.GetComplexValues(RowWithId(table, "row1"), "multi-value-data")
                 .Select(v => (string)v["Value"]!));
    }

    [Fact]
    public void AValueAddedToARowThatAlreadyHasSome_JoinsThemRatherThanReplacing()
    {
        if (!TryStage()) return;

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("Table1");
            t.AddComplexValue(RowWithId(t, "row2"), "multi-value-data", new Row { ["Value"] = "value9" });
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("Table1");

        Assert.Equal(
            new[] { "value1", "value4", "value9" },
            table.GetComplexValues(RowWithId(table, "row2"), "multi-value-data")
                 .Select(v => (string)v["Value"]!));
    }

    /// <summary>The link is per row, so other rows' values must not move.</summary>
    [Fact]
    public void AddingToOneRow_LeavesTheOtherRowsAlone()
    {
        if (!TryStage()) return;

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("Table1");
            t.AddComplexValue(RowWithId(t, "row1"), "multi-value-data", new Row { ["Value"] = "added" });
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("Table1");

        Assert.Equal(4, table.GetComplexValues(RowWithId(table, "row3"), "multi-value-data").Count);
        Assert.Empty(table.GetComplexValues(RowWithId(table, "row4"), "multi-value-data"));
    }

    [Fact]
    public void AnAttachmentIsAddedWithItsFileMetadata()
    {
        if (!TryStage()) return;

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("Table1");
            t.AddComplexValue(RowWithId(t, "row1"), "attach-data", new Row
            {
                ["FileName"]  = "written.txt",
                ["FileType"]  = "txt",
                ["FileFlags"] = 0,
            });
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("Table1");

        Row added = Assert.Single(table.GetComplexValues(RowWithId(table, "row1"), "attach-data"));
        Assert.Equal("written.txt", added["FileName"]);
        Assert.Equal("txt", added["FileType"]);

        // The attachments already on rows 2 and 4 are untouched.
        Assert.Equal(2, table.GetComplexValues(RowWithId(table, "row2"), "attach-data").Count);
        Assert.Single(table.GetComplexValues(RowWithId(table, "row4"), "attach-data"));
    }

    /// <summary>The flat row's own id counts across the table, so two additions must not collide.</summary>
    [Fact]
    public void TheFlatRowsOwnIdContinuesAcrossTheWholeTable()
    {
        if (!TryStage()) return;

        using var db = Database.Open(_path);
        var t = db.GetTable("Table1");

        Row first  = t.AddComplexValue(RowWithId(t, "row1"), "multi-value-data", new Row { ["Value"] = "a" });
        Row second = t.AddComplexValue(RowWithId(t, "row4"), "multi-value-data", new Row { ["Value"] = "b" });

        const string OwnId = "Table1_multi-value-data";
        Assert.True((int)first[OwnId]! > 6, "the fixture already uses 1..6, so a new id continues past them");
        Assert.NotEqual((int)first[OwnId]!, (int)second[OwnId]!);
    }

    [Fact]
    public void AddingToSomethingThatIsNotAComplexColumn_SaysSo()
    {
        if (!TryStage()) return;

        using var db = Database.Open(_path);
        var t = db.GetTable("Table1");

        var ex = Assert.Throws<InvalidOperationException>(
            () => t.AddComplexValue(RowWithId(t, "row1"), "memo-data", new Row { ["Value"] = "x" }));
        Assert.Contains("not a complex column", ex.Message);
    }

    /// <summary>
    /// Writing a Memo or OLE value into this .accdb still fails: the long-value usage-map reference
    /// parsed out of its table definition is not a real page, so the writer reads far past the end
    /// of the file. Pinned here so it announces itself when fixed — the complex-column writes above
    /// do not go through that path.
    /// </summary>
    [Fact]
    public void WritingAMemoValueIntoAnAccdb_DoesNotWorkYet()
    {
        if (!TryStage()) return;

        using var db = Database.Open(_path);
        var t = db.GetTable("Table1");

        Assert.ThrowsAny<Exception>(
            () => t.Insert(new Row { ["id"] = "with-memo", ["memo-data"] = "hello" }));
    }
}
