using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Files past the first usage-map window, and inserts that cost the same however large the table is
/// (qcatco/exportmdb.interop#20). A table's pages are listed in a usage map whose inline form covers one window of
/// pages - 512 in Access's own files, 1,600 in this engine's - and adding a page outside it threw, so no file could
/// grow past that. And every insert read every page the table owned to look for space, so each was slower than the
/// last.
/// </summary>
public sealed class LargeFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"large_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static IReadOnlyList<Column> DocumentColumns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(100).Build(),
        new ColumnBuilder("Body", DataType.Memo).Build(),
    };

    /// <summary>A memo of 3,000 characters, different for each row, so a mix-up shows.</summary>
    private static string Body(int i) => $"{i:D6} " + new string((char)('a' + i % 26), 2_993);

    [Fact]
    public void Rows_past_the_first_usage_map_window_are_written_and_read_back_in_order()
    {
        // A memo this long is stored on its own long-value page, so 2,000 rows take some 2,000 of them, plus the
        // data pages: past the 1,600 pages one inline map covers, for the memo column's map at least.
        const int count = 2_000;
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Documents", DocumentColumns());
            for (int i = 0; i < count; i++)
                table.Insert(new Row { ["Id"] = i, ["Name"] = "Document " + i, ["Body"] = Body(i) });
        }

        Assert.True(new FileInfo(_path).Length > 1_600L * 4096, "the file must pass one inline map's window");
        using (var db = Database.Open(_path))
        {
            var rows = db.GetTable("Documents").ReadAllRows();
            Assert.Equal(count, rows.Count);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(i, rows[i]["Id"]);
                Assert.Equal("Document " + i, rows[i]["Name"]);
                Assert.Equal(Body(i), rows[i]["Body"]);
            }
        }
    }

    [Fact]
    public void Each_insert_reads_about_as_many_pages_however_large_the_table()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Documents", DocumentColumns());

        long ReadsFor(int from, int count)
        {
            long before = db.File.ReadCount;
            for (int i = from; i < from + count; i++)
                table.Insert(new Row { ["Id"] = i, ["Name"] = "Document " + i, ["Body"] = Body(i) });
            return db.File.ReadCount - before;
        }

        long first = ReadsFor(0, 500);
        long later = ReadsFor(500, 500);

        // Scanning every owned page made the second 500 inserts read several times as many pages as the first.
        Assert.True(later <= first + first / 5,
            $"the first 500 inserts read {first} pages and the next 500 read {later}: inserts must not slow as the table grows");
    }

    [Fact]
    public void A_data_page_holds_at_most_255_rows()
    {
        // Jet addresses a row on its page with one byte, so a page of more than 255 rows has rows nothing can point
        // at. One-byte rows would otherwise fit about 400 to a page.
        const int count = 600;
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Tiny", new[] { new ColumnBuilder("B", DataType.Byte).Build() });
            for (int i = 0; i < count; i++)
                table.Insert(new Row { ["B"] = (byte)(i % 256) });
        }

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Tiny");
            var def = table.Definition;
            var format = db.File.Format;
            var owned = UsageMap.GetOwnedPages(db.File.ReadPage(def.UmapPageNumber), def.OwnedPagesRow, format, db.File);
            Assert.All(owned, page =>
                Assert.InRange(ByteUtil.GetShort(db.File.ReadPage(page), format.OffsetDataNumRows), 1, 255));
            Assert.Equal(count, table.ReadAllRows().Count);
        }
    }
}
