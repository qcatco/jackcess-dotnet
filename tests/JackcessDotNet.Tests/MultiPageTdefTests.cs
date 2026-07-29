using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tables whose definition is longer than one page and continues on the page named at bytes 4-7.
/// <para>
/// Reading only the first page left the index sections beyond the end of the buffer, which used to
/// be handled by reporting no indexes at all — so a wide table looked unindexed, and appending to it
/// left every index untouched while Access went on reading through them.
/// </para>
/// </summary>
public sealed class MultiPageTdefTests : IDisposable
{
    private readonly string _path;
    public MultiPageTdefTests()
        => _path = Path.Combine(Path.GetTempPath(), $"widetdef_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>
    /// 120 columns with long names. Each costs 25 bytes of definition plus a length-prefixed UTF-16
    /// name, so this runs past a 4 KB page and forces a continuation.
    /// </summary>
    private const int ColumnCount = 120;

    private static string ColumnName(int i) => $"Column_{i:D3}_with_a_deliberately_long_name";

    private static IReadOnlyList<Column> WideColumns()
    {
        var columns = new List<Column> { new ColumnBuilder("Id", DataType.Long).Build() };
        for (int i = 1; i < ColumnCount; i++)
            columns.Add(new ColumnBuilder(ColumnName(i), DataType.Long).Build());
        return columns;
    }

    private void CreateWideTable()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.CreateTable("Wide", WideColumns(), primaryKey: "Id");
    }

    [Fact]
    public void TheDefinitionActuallySpansMoreThanOnePage()
    {
        CreateWideTable();

        using var db = Database.Open(_path);
        int tdefPage = db.GetTable("Wide").Definition.TdefPageNumber;

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] first = pf.ReadPage(tdefPage);

        int next = ByteUtil.GetInt(first, 4);
        Assert.True(next > 0, "the fixture is supposed to need a continuation page");
        Assert.Equal(JetFormat.PageTypeTableDef, pf.ReadPage(next)[0]);
        Assert.True(ByteUtil.GetInt(first, 8) > JetFormat.Jet4.PageSize - 8,
            "the definition should be longer than a single page holds");
    }

    [Fact]
    public void EveryColumnIsReadBack()
    {
        CreateWideTable();

        using var db = Database.Open(_path);
        var table = db.GetTable("Wide");

        Assert.Equal(ColumnCount, table.Columns.Count);
        Assert.Equal("Id", table.Columns[0].Name);
        for (int i = 1; i < ColumnCount; i++)
            Assert.Equal(ColumnName(i), table.Columns[i].Name);
    }

    /// <summary>The index sections live past the page boundary — the part that used to be dropped.</summary>
    [Fact]
    public void ThePrimaryKeyIndexIsReadBack()
    {
        CreateWideTable();

        using var db = Database.Open(_path);
        var ix = Assert.Single(db.GetTable("Wide").Indexes);

        Assert.Equal("PrimaryKey", ix.Name);
        Assert.True(ix.IsPrimaryKey);
        Assert.Equal("Id", Assert.Single(ix.Columns).Column.Name);
        Assert.True(ix.RootPageNumber > 0);
    }

    [Fact]
    public void RowsInsertIntoAWideTableAndAreFoundThroughItsIndex()
    {
        const int Rows = 60;
        CreateWideTable();

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Wide");
            for (int i = 0; i < Rows; i++)
            {
                var row = new Row { ["Id"] = i };
                for (int c = 1; c < ColumnCount; c++) row[ColumnName(c)] = c * 1000 + i;
                table.Insert(row);
            }
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("Wide");

        Assert.Equal(Rows, reread.ReadAllRows().Count);
        for (int i = 0; i < Rows; i++)
        {
            Row? found = reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i);
            Assert.NotNull(found);
            Assert.Equal(1000 + i, found![ColumnName(1)]);
        }
    }

    /// <summary>Splicing an index into a definition that is already chained.</summary>
    [Fact]
    public void ASecondaryIndexCanBeAddedToAWideTable()
    {
        CreateWideTable();

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Wide");
            for (int i = 0; i < 20; i++)
            {
                var row = new Row { ["Id"] = i };
                for (int c = 1; c < ColumnCount; c++) row[ColumnName(c)] = c * 1000 + i;
                table.Insert(row);
            }
        }

        using (var db = Database.Open(_path))
            db.CreateIndex("Wide", "ByCol1", ColumnName(1));

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("Wide");

        Assert.Equal(2, reread.Indexes.Count);
        var added = Assert.Single(reread.Indexes, i => i.Name == "ByCol1");
        Assert.Equal(ColumnName(1), Assert.Single(added.Columns).Column.Name);

        // Backfilled, so it answers straight away.
        for (int i = 0; i < 20; i++)
            Assert.NotNull(reread.NewIndexCursor("ByCol1").FindRow(ColumnName(1), 1000 + i));

        // And the primary key still works — the splice rewrote every page of the definition.
        for (int i = 0; i < 20; i++)
            Assert.NotNull(reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i));
    }
}
