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
        => table.ReadAllRows().Single(
            r => r.TryGetValue("id", out object? v) && (string?)v == id);

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
    /// A Memo value goes through the long-value store, which needs the column's usage map. Access
    /// precedes those map references with a count that this reader used to consume as part of the
    /// first reference, producing a page number in the millions, so the write read far past the end
    /// of the file. Reading a Memo never noticed: that follows the reference held in the row itself
    /// and consults the map only to allocate.
    /// <para>
    /// With the count skipped the write completes and round-trips <em>here</em> — but the ACE engine
    /// reads the value back as empty, so it is not yet right, and this asserts only what is true.
    /// The row itself is visible to Access; its long-value chain is not. Access also counts one row
    /// fewer in this table than this library does after the write, which is likely the same defect
    /// seen from the other side.
    /// </para>
    /// </summary>
    [Fact]
    public void AMemoValueWrittenIntoAnAccdb_RoundTripsHereButIsNotYetReadableByAccess()
    {
        if (!TryStage()) return;

        string memo = "hello from the writer, " + new string('m', 400);

        using (var db = Database.Open(_path))
            db.GetTable("Table1").Insert(new Row { ["id"] = "with-memo", ["memo-data"] = memo });

        using var reopened = Database.Open(_path);
        Row written = RowWithId(reopened.GetTable("Table1"), "with-memo");

        Assert.Equal(memo, written["memo-data"]);
    }
}
