using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The review of qcatco/jackcess-dotnet#2, finding by finding. Files may now grow past 128 MB, which made an old limit
/// reachable: a row's address was kept as (page &lt;&lt; 16 | row), so a page past 32,767 was written into a
/// primary-key index wrongly. The refusal of text longer than its column came after an update had deleted the row it
/// was replacing. And the space deletes freed was kept for the whole file, so every long-value write, in any table, read
/// every page freed so far to see whether it was its own.
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
    [InlineData(32_767)]   // the row goes to page 32,768: shifted 16 bits, its address turned negative
    [InlineData(40_000)]
    [InlineData(65_535)]   // page 65,536: its address wrapped to page 0
    [InlineData(70_000)]
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
        var body = new string('ж', 1_500);
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("People", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(5).Build(),
                new ColumnBuilder("Body", DataType.Memo).Build(),
            }, primaryKey: "Id");
            table.Insert(new Row { ["Id"] = 1, ["Name"] = "abc", ["Body"] = body });
            table.Insert(new Row { ["Id"] = 2, ["Name"] = "def" });

            Assert.Throws<ColumnValueTooLongException>(() => table.UpdateByPrimaryKey(1, new Row { ["Name"] = "abcdefgh" }));

            AssertRowOneAsItWas(table, body);
        }

        using (var db = Database.Open(_path))
            AssertRowOneAsItWas(db.GetTable("People"), body);   // in the file too
    }

    private static void AssertRowOneAsItWas(Table table, string body)
    {
        var rows = table.ReadAllRows().OrderBy(r => (int)r["Id"]!).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("abc", rows[0]["Name"]);
        Assert.Equal(body, rows[0]["Body"]);
        var found = table.NewIndexCursor().FindRowByPrimaryKey(1);
        Assert.NotNull(found);
        Assert.Equal("abc", found["Name"]);
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

    private static IReadOnlyList<Column> BlobColumns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Blob", DataType.Ole).Build(),
    };

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Runs <paramref name="scenario"/> on a database of its own, deleted afterwards.</summary>
    private static T InAnotherDatabase<T>(Func<Database, T> scenario)
    {
        string path = Path.Combine(Path.GetTempPath(), $"review_{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path, JetVersion.Jet4);
            return scenario(db);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The pages each insert into <paramref name="table"/> of a 3,000-byte value reads.</summary>
    private static long[] ReadsForInserts(Database db, Table table, int count)
    {
        var reads = new long[count];
        for (int i = 0; i < count; i++)
        {
            long before = db.File.ReadCount;
            table.Insert(new Row { ["Id"] = 100 + i, ["Blob"] = Bytes(3_000, 100 + i) });
            reads[i] = db.File.ReadCount - before;
        }
        return reads;
    }

    [Fact]
    public void Deleting_a_long_value_in_one_table_does_not_slow_long_value_inserts_in_another()
    {
        // Pages a delete freed were kept for the whole file, and every long-value write in any table read each of them
        // and its own map to see whether it was one of its own: deleting 1 MB in one table cost each later write in
        // another some 250 reads.
        long[] InsertsIntoB(bool deleteFromA) => InAnotherDatabase(db =>
        {
            var a = db.CreateTable("A", BlobColumns());
            var b = db.CreateTable("B", BlobColumns());
            a.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(1_000_000, 1) });
            b.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(3_000, 2) });
            if (deleteFromA) a.DeleteRow("Id", 1);
            return ReadsForInserts(db, b, 3);
        });

        Assert.Equal(InsertsIntoB(deleteFromA: false), InsertsIntoB(deleteFromA: true));
    }

    [Fact]
    public void Space_a_deleted_long_value_freed_is_used_again_without_reading_every_page_it_freed()
    {
        // Each later write in the column read every page the deleted value had freed until writes had filled them: some
        // 500 reads a write after a 1 MB value went.
        (long[] reads, int pagesAdded) InsertsAfterABigValue(bool deleteIt) => InAnotherDatabase(db =>
        {
            var table = db.CreateTable("T", BlobColumns());
            table.Insert(new Row { ["Id"] = 1, ["Blob"] = Bytes(1_000_000, 1) });
            if (deleteIt) table.DeleteRow("Id", 1);
            int pagesBefore = LvalPages(db, table, "Blob").Count;
            long[] reads = ReadsForInserts(db, table, 5);
            return (reads, LvalPages(db, table, "Blob").Count - pagesBefore);
        });

        var kept = InsertsAfterABigValue(deleteIt: false);
        var deleted = InsertsAfterABigValue(deleteIt: true);

        Assert.Equal(0, deleted.pagesAdded);   // the five values went where the deleted one had been
        for (int i = 0; i < deleted.reads.Length; i++)
            Assert.True(deleted.reads[i] <= kept.reads[i],
                $"insert {i} read {deleted.reads[i]} pages into freed space and {kept.reads[i]} onto a new page");
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
