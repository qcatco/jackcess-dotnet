using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Covers growing a table's owned-pages usage map past the window its inline
/// bitmap was allotted. Access-authored files hand out bitmaps as small as
/// 64 bytes — 512 pages, i.e. the first ~2 MB of a Jet4 file — so appending to a
/// real-world .mdb runs off the end of the window almost immediately. Jackcess
/// Java answers that by sliding the window
/// (<c>UsageMap.InlineHandler.moveToNewStartPage</c>) and only promotes to a
/// reference map when the owned span itself outgrows the bitmap.
/// </summary>
public sealed class UsageMapGrowthTests : IDisposable
{
    private readonly string _path;
    public UsageMapGrowthTests()
        => _path = Path.Combine(Path.GetTempPath(), $"umap_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a usage-map page holding two inline maps whose bitmaps are
    /// <paramref name="bitmapSize"/> bytes each. That size is the knob Access
    /// varies per file and <see cref="UsageMap.CreateUmapPage"/> pins to the
    /// format default, so crafting the page is the only way to reproduce the
    /// narrow windows real templates ship with.
    /// </summary>
    private static byte[] CraftUmapPage(JetFormat format, int bitmapSize, int startPage = 0)
    {
        int rowDataSize = 1 + 4 + bitmapSize;   // MAP_TYPE + startPage + bitmap
        var page = new byte[format.PageSize];

        page[0] = JetFormat.PageTypeUsageMap;
        page[1] = 0x01;

        // Rows are packed from the end of the page; row 0 sits at the higher address.
        int row0Start = format.PageSize - rowDataSize;
        int row1Start = row0Start - rowDataSize;
        ByteUtil.PutInt(page, row0Start + 1, startPage);   // map type 0x00 = inline (already zero)

        ByteUtil.PutShort(page, format.OffsetDataRowTable, (short)row0Start);
        ByteUtil.PutShort(page, format.OffsetDataRowTable + JetFormat.SizeRowEntry, (short)row1Start);
        ByteUtil.PutShort(page, format.OffsetDataNumRows, 2);
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace,
            (short)(row1Start - format.OffsetDataRowTable - 2 * JetFormat.SizeRowEntry));

        return page;
    }

    private static int StartPageOf(byte[] page, int mapRow, JetFormat format)
        => ByteUtil.GetInt(page, UsageMap.GetRowStart(page, mapRow, format) + 1);

    // ── The window has to follow the table ────────────────────────────────────

    [Fact]
    public void AddPage_PastEndOfWindow_SlidesWindowUpAndKeepsEveryPage()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64);   // 512 pages from page 0

        UsageMap.AddPage(page, 0, 100, format);
        UsageMap.AddPage(page, 0, 101, format);

        // 600 is outside 0..511, but the owned span 100..600 still fits in 512
        // pages, so the window slides to the multiple of 8 at or below 100.
        UsageMap.AddPage(page, 0, 600, format);

        Assert.Equal(new[] { 100, 101, 600 }, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(96, StartPageOf(page, 0, format));
    }

    [Fact]
    public void AddPage_BeforeStartOfWindow_SlidesWindowDown()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64, startPage: 1000);

        UsageMap.AddPage(page, 0, 1000, format);
        UsageMap.AddPage(page, 0, 990, format);

        Assert.Equal(new[] { 990, 1000 }, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(984, StartPageOf(page, 0, format));
    }

    [Fact]
    public void AddPage_IntoEmptyMap_RebasesWindowOnThatPage()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64);   // 512 pages from page 0

        // An empty table's map owns nothing, so it can rebase anywhere — this is
        // the common case for a template whose target table ships empty.
        UsageMap.AddPage(page, 0, 5000, format);

        Assert.Equal(new[] { 5000 }, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(5000, StartPageOf(page, 0, format));
    }

    [Fact]
    public void AddPage_FillsAWholeWindowFarBeyondTheOriginalOne()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 8);   // 64 pages from page 0

        // The shape that broke in production: a table that ships empty inside a
        // large file, then grows page by page way past its original window. The
        // first add rebases; the rest land in range.
        var expected = new List<int>();
        for (int p = 5000; p < 5064; p++)
        {
            UsageMap.AddPage(page, 0, p, format);
            expected.Add(p);
        }

        Assert.Equal(expected, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(5000, StartPageOf(page, 0, format));

        // One more page and the owned span itself outgrows the bitmap — no
        // placement of the window can cover it, so it needs a reference map.
        Assert.Throws<NotSupportedException>(() => UsageMap.AddPage(page, 0, 5064, format));
    }

    [Fact]
    public void AddPage_SlidingBuysExactlyOneBitmapFromTheTablesFirstPage()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64);   // 512 pages from page 0

        // A table whose first data page sits at 112 — an append to a ~450 KB
        // Access template. Sliding lets it use its bitmap's full worth of pages
        // wherever they sit in the file, but not one page more: the owned *span*,
        // not the window's position, is the inline ceiling. Driven through the
        // store-less overload, so the run stops where sliding stops rather than
        // carrying on into promotion.
        int lastAccepted = 0;
        for (int p = 112; p < 2000; p++)
        {
            try { UsageMap.AddPage(page, 0, p, format); }
            catch (NotSupportedException) { break; }
            lastAccepted = p;
        }

        Assert.Equal(112 + 512 - 1, lastAccepted);
        Assert.Equal(112, StartPageOf(page, 0, format));
    }

    [Fact]
    public void AddPage_SpanWiderThanWindow_WithoutAPageStore_CannotPromote()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64);   // 512 pages

        UsageMap.AddPage(page, 0, 10, format);

        // 10..5000 cannot fit in 512 pages however the window is placed, so the map
        // has to be promoted — and promotion allocates bitmap pages, which this
        // overload has no way to do.
        var ex = Assert.Throws<NotSupportedException>(
            () => UsageMap.AddPage(page, 0, 5000, format));
        Assert.Contains("reference", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Refusing must leave the map exactly as it was.
        Assert.Equal(new[] { 10 }, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(0, page[UsageMap.GetRowStart(page, 0, format)]);   // still inline
    }

    [Fact]
    public void AddPage_AlreadyOwnedPage_IsANoOp()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64);

        UsageMap.AddPage(page, 0, 300, format);
        UsageMap.AddPage(page, 0, 300, format);

        Assert.Equal(new[] { 300 }, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(0, StartPageOf(page, 0, format));   // in range, so no slide
    }

    // ── Promotion to a reference-style map ────────────────────────────────────

    private static int PagesPerBitmapPage(JetFormat format) => (format.PageSize - 4) * 8;
    private static int BitmapPagePointers(int bitmapSize)   => (bitmapSize + 5 - 1) / 4;

    /// <summary>
    /// A page store over a fresh file with page 0 already taken. Every real database has
    /// its header there, and a reference row stores 0 to mean "no bitmap page yet" — so a
    /// bitmap page must never be allocated at page 0.
    /// </summary>
    private PageFile NewPageStore(JetFormat format)
    {
        var file = new PageFile(_path, format, FileMode.Create);
        file.WritePage(0, new byte[format.PageSize]);
        return file;
    }

    [Fact]
    public void AddPage_SpanWiderThanWindow_PromotesAndKeepsEveryPage()
    {
        var format = JetFormat.Jet4;
        using var file = NewPageStore(format);
        var allocator = new PageAllocator(file);

        var page = CraftUmapPage(format, bitmapSize: 64);   // 512 pages
        UsageMap.AddPage(page, 0, 10,   format, allocator, file);
        UsageMap.AddPage(page, 0, 5000, format, allocator, file);   // no window covers both

        Assert.Equal(0x01, page[UsageMap.GetRowStart(page, 0, format)]);   // reference-style now
        Assert.Equal(new[] { 10, 5000 }, UsageMap.GetOwnedPages(page, 0, format, file));
    }

    [Fact]
    public void AddPage_ReferenceMap_SpansSeveralBitmapPages()
    {
        var format = JetFormat.Jet4;
        using var file = NewPageStore(format);
        var allocator = new PageAllocator(file);
        int perBitmapPage = PagesPerBitmapPage(format);

        var page = CraftUmapPage(format, bitmapSize: 64);
        UsageMap.AddPage(page, 0, 10, format, allocator, file);

        // Straddle the boundary between the first and second bitmap pages.
        int far = perBitmapPage + 5;
        UsageMap.AddPage(page, 0, far,     format, allocator, file);
        UsageMap.AddPage(page, 0, far + 1, format, allocator, file);
        UsageMap.AddPage(page, 0, 11,      format, allocator, file);

        Assert.Equal(new[] { 10, 11, far, far + 1 },
                     UsageMap.GetOwnedPages(page, 0, format, file));
    }

    [Fact]
    public void AddPage_BeyondWhatThePointerArrayAddresses_Throws()
    {
        var format = JetFormat.Jet4;
        using var file = NewPageStore(format);
        var allocator = new PageAllocator(file);

        var page = CraftUmapPage(format, bitmapSize: 64);
        UsageMap.AddPage(page, 0, 10, format, allocator, file);

        // 17 pointers × 32,736 pages is the ceiling for a 69-byte row (~2.2 GB at
        // Jet4, i.e. past Access's own file-size limit).
        int tooFar = BitmapPagePointers(64) * PagesPerBitmapPage(format);

        Assert.False(UsageMap.CanAddPage(page, 0, tooFar, format));
        Assert.Throws<NotSupportedException>(
            () => UsageMap.AddPage(page, 0, tooFar, format, allocator, file));

        // Still inline and untouched — a refusal costs nothing.
        Assert.Equal(new[] { 10 }, UsageMap.GetOwnedPages(page, 0, format));
    }

    // ── A refusal must be knowable before a page is allocated ─────────────────

    [Fact]
    public void CanAddPage_ReportsWhatAddPageWillAccept()
    {
        var format = JetFormat.Jet4;
        var page = CraftUmapPage(format, bitmapSize: 64);
        UsageMap.AddPage(page, 0, 100, format);

        Assert.True(UsageMap.CanAddPage(page, 0, 600, format));    // a slide covers it
        Assert.True(UsageMap.CanAddPage(page, 0, 5000, format));   // promotion covers it
        Assert.False(UsageMap.CanAddPage(page, 0,
            BitmapPagePointers(64) * PagesPerBitmapPage(format), format));   // nothing covers it

        // Asking must not have changed anything.
        Assert.Equal(new[] { 100 }, UsageMap.GetOwnedPages(page, 0, format));
        Assert.Equal(0, StartPageOf(page, 0, format));
    }

    [Fact]
    public void Insert_PastTheUsageMapWindow_PromotesAndKeepsEveryRow()
    {
        // The production shape, end to end: a table sitting low in a file that has
        // grown well past the 1600 pages its bitmap can address from page 0. No slide
        // can span that, so the map must go reference-style and every row must still
        // be readable back through the normal reader afterwards.
        const int WideColumns = 7;
        const int SeedRows = 4;
        const int RowsAfterPadding = 60;
        var fat = new string('x', 255);

        var columns = new List<Column> { new ColumnBuilder("Id", DataType.Long).Build() };
        for (int c = 0; c < WideColumns; c++)
            columns.Add(new ColumnBuilder($"Pad{c}", DataType.Text).MaxLength(255).Build());

        Row FatRow(int id)
        {
            var row = new Row { ["Id"] = id };
            for (int c = 0; c < WideColumns; c++) row[$"Pad{c}"] = fat;
            return row;
        }

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Wide", columns);
            for (int i = 0; i < SeedRows; i++) table.Insert(FatRow(i));
        }

        // Pad with blank pages, exactly what PageAllocator.AllocatePage writes.
        using (var pad = new FileStream(_path, FileMode.Append, FileAccess.Write))
            pad.Write(new byte[1700 * JetFormat.Jet4.PageSize]);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable("Wide");
            for (int i = 0; i < RowsAfterPadding; i++)
                table.Insert(FatRow(1000 + i));
        }

        using (var db = Database.Open(_path))
        {
            var rows = db.GetTable("Wide").ReadAllRows();
            Assert.Equal(SeedRows + RowsAfterPadding, rows.Count);
            Assert.All(rows, r => Assert.Equal(fat, r["Pad0"]));
        }
    }
}
