using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Space a delete freed is used again after the file is closed and opened again (qcatco/exportmdb.interop#23), as
/// Access keeps it: a long-value column's free-space usage map lists the pages a delete left room on, a page leaves it
/// once a value fills it, and the first write to the column in a session starts from it.
/// </summary>
public sealed class CrossSessionFreedSpaceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"freed_sessions_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static Column[] BlobColumns() => new[]
    {
        new ColumnBuilder("Id", DataType.Long).Build(),
        new ColumnBuilder("Blob", DataType.Ole).Build(),
    };

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static List<int> OwnedPages(Database db, Table table)
    {
        var lval = table.Definition.LvalColumnUmapPages["Blob"];
        return UsageMap.GetOwnedPages(db.File.ReadPage(lval.OwnedPage), lval.OwnedRow, db.File.Format, db.File);
    }

    private static List<int> FreeSpacePages(Database db, Table table)
    {
        var lval = table.Definition.LvalColumnUmapPages["Blob"];
        return UsageMap.GetOwnedPages(db.File.ReadPage(lval.FreePage), lval.FreeRow, db.File.Format, db.File);
    }

    private static int Room(Database db, int page) => ByteUtil.GetShort(db.File.ReadPage(page), JetFormat.OffsetDataFreeSpace);

    // Ten 2,000-byte values go two to a page; deleting every second leaves five pages each with 2,080 bytes free.
    private List<int> FiveHalfFreePages()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("T", BlobColumns());
        for (int i = 1; i <= 10; i++)
            table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(2_000, i) });
        for (int i = 2; i <= 10; i += 2)
            table.DeleteRow("Id", i);
        return OwnedPages(db, table).Where(p => Room(db, p) > 2_000).ToList();
    }

    [Fact]
    public void The_free_space_map_lists_the_pages_a_delete_left_room_on()
    {
        var freed = FiveHalfFreePages();

        using var db = Database.Open(_path);
        Assert.Equal(5, freed.Count);
        Assert.Equal(freed, FreeSpacePages(db, db.GetTable("T")));
    }

    [Fact]
    public void Space_a_delete_freed_in_an_earlier_session_is_used_again()
    {
        FiveHalfFreePages();

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("T");
            int before = OwnedPages(db, table).Count;
            for (int i = 11; i <= 15; i++)
                table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(1_500, i) });

            Assert.Equal(before, OwnedPages(db, table).Count);   // each value took a freed page's room, not a new page
        }

        using (var db = Database.Open(_path))
        {
            var rows = db.GetTable("T").ReadAllRows().ToDictionary(r => (int)r["Id"]!);
            Assert.Equal(10, rows.Count);
            for (int i = 1; i <= 9; i += 2)
                Assert.Equal(Bytes(2_000, i), rows[i]["Blob"]);
            for (int i = 11; i <= 15; i++)
                Assert.Equal(Bytes(1_500, i), rows[i]["Blob"]);
        }
    }

    [Fact]
    public void A_page_a_value_fills_leaves_the_free_space_map()
    {
        var freed = FiveHalfFreePages();

        using var db = Database.Open(_path);
        var table = db.GetTable("T");
        for (int i = 11; i <= 15; i++)
            table.Insert(new Row { ["Id"] = i, ["Blob"] = Bytes(2_050, i) });   // leaves each page under 64 bytes free

        Assert.All(freed, p => Assert.True(Room(db, p) < FreedLvalPages.MinUsefulFreeSpace, $"page {p}: {Room(db, p)} free"));
        Assert.Empty(FreeSpacePages(db, table));
    }
}
