using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The review of qcatco/jackcess-dotnet#2, finding by finding. Files may now grow past 128 MB, which made an old limit
/// reachable: a row's address was kept as (page &lt;&lt; 16 | row), so a page past 32,767 was written into a
/// primary-key index wrongly. And the refusal of text longer than its column came after an update had deleted the
/// row it was replacing.
/// </summary>
public sealed class ReviewFindingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"review_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(100).Build(),
        new ColumnBuilder("Body", DataType.Memo).Build(),
    };

    private static List<int> LvalPages(Database db, Table table, string column)
    {
        var lval = table.Definition.LvalColumnUmapPages[column];
        return UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File);
    }

    [Theory]
    [InlineData(40_000)]   // past 32,767: shifted 16 bits the page no longer fitted
    [InlineData(70_000)]   // past 65,535
    public void A_row_on_a_page_past_32767_is_indexed_and_found_by_its_primary_key(int padTo)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Keyed", Columns(), primaryKey: "Id");
            db.File.WritePage(padTo, new byte[db.File.Format.PageSize]);   // the next page allocated is padTo + 1
            table.Insert(new Row { ["Id"] = 7, ["Name"] = "far out" });
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Keyed");
            var reader = new IndexReader(db.File, table.Indexes.Single(ix => ix.IsPrimaryKey));
            int pointer = reader.FindRowPointers(7).Single();
            Assert.Equal(padTo + 1, RowPointer.Page(pointer));   // what the leaf entry on disk says - what ACE reads
            Assert.Equal(0, RowPointer.Row(pointer));
            Assert.Equal("far out", table.NewIndexCursor().FindRowByPrimaryKey(7)?["Name"]);
        }
    }

    [Fact]
    public void An_update_refused_for_text_longer_than_its_column_leaves_the_row_as_it_was()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("People", new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(5).Build(),
            new ColumnBuilder("Body", DataType.Memo).Build(),
        }, primaryKey: "Id");
        var body = new string('ж', 1_500);
        table.Insert(new Row { ["Id"] = 1, ["Name"] = "abc", ["Body"] = body });
        table.Insert(new Row { ["Id"] = 2, ["Name"] = "def" });

        Assert.Throws<ColumnValueTooLongException>(() => table.UpdateByPrimaryKey(1, new Row { ["Name"] = "abcdefgh" }));

        var rows = table.ReadAllRows().OrderBy(r => (int)r["Id"]!).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("abc", rows[0]["Name"]);
        Assert.Equal(body, rows[0]["Body"]);
    }

    [Fact]
    public void A_Jet3_text_column_counts_its_length_in_characters()
    {
        string? source = TestCorpus.File("V1997", "common1V1997.mdb");
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path);
        using var db = Database.Open(_path);
        var table = db.ListTables().Select(db.GetTable).First(t => t.Columns.Any(c => c.DataType == DataType.Text));
        var col = table.Columns.First(c => c.DataType == DataType.Text);

        table.Insert(new Row { [col.Name] = new string('x', col.Length) });

        Assert.Throws<ColumnValueTooLongException>(() => table.Insert(new Row { [col.Name] = new string('x', col.Length + 1) }));
    }

    [Fact]
    public void Long_values_share_the_column_s_last_page_while_it_has_room()
    {
        // 750 Cyrillic characters do not compress: 1,500 bytes, kept on long-value pages, two to a page.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Notes", Columns());
        for (int i = 0; i < 3; i++)
            table.Insert(new Row { ["Id"] = i, ["Name"] = "n", ["Body"] = new string((char)('а' + i), 750) });

        Assert.Equal(2, LvalPages(db, table, "Body").Count);
        Assert.Equal(new string('в', 750), table.ReadAllRows()[2]["Body"]);
    }

    [Fact]
    public void Space_freed_in_one_long_value_column_is_not_used_by_another()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Notes", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("A",  DataType.Memo).Build(),
            new ColumnBuilder("B",  DataType.Memo).Build(),
        });
        table.Insert(new Row { ["Id"] = 1, ["A"] = new string('я', 3_000) });
        table.DeleteRow("Id", 1);

        table.Insert(new Row { ["Id"] = 2, ["B"] = new string('ж', 1_000) });

        Assert.NotEmpty(LvalPages(db, table, "B"));
        Assert.Empty(LvalPages(db, table, "A").Intersect(LvalPages(db, table, "B")));
        Assert.Equal(new string('ж', 1_000), table.ReadAllRows().Single()["B"]);
    }

    [Fact]
    public void A_row_never_goes_onto_another_table_s_page_listed_in_its_map()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var mine = db.CreateTable("Mine", Columns());
        var other = db.CreateTable("Other", Columns());
        mine.Insert(new Row { ["Id"] = 1, ["Name"] = "mine" });
        other.Insert(new Row { ["Id"] = 1, ["Name"] = "other" });
        int othersPage = UsageMap.GetLastPage(db.File, other.Definition.UmapPageNumber, other.Definition.OwnedPagesRow);
        UsageMap.AddPage(db.File, new PageAllocator(db.File), mine.Definition.UmapPageNumber, mine.Definition.OwnedPagesRow, othersPage);

        mine.Insert(new Row { ["Id"] = 2, ["Name"] = "mine too" });

        Assert.Equal(new[] { "other" }, other.ReadAllRows().Select(r => (string)r["Name"]!).ToArray());
    }

    [Fact]
    public void A_long_value_never_goes_onto_a_page_that_is_not_a_long_value_page()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var mine = db.CreateTable("Mine", Columns());
        var other = db.CreateTable("Other", Columns());
        other.Insert(new Row { ["Id"] = 1, ["Name"] = "other" });
        int othersPage = UsageMap.GetLastPage(db.File, other.Definition.UmapPageNumber, other.Definition.OwnedPagesRow);
        var lval = mine.Definition.LvalColumnUmapPages["Body"];
        UsageMap.AddPage(db.File, new PageAllocator(db.File), lval.OwnedPage, lval.OwnedRow, othersPage);

        mine.Insert(new Row { ["Id"] = 1, ["Name"] = "mine", ["Body"] = new string('ж', 1_000) });

        Assert.Equal(new[] { "other" }, other.ReadAllRows().Select(r => (string)r["Name"]!).ToArray());
        Assert.Equal(new string('ж', 1_000), mine.ReadAllRows().Single()["Body"]);
    }
}
