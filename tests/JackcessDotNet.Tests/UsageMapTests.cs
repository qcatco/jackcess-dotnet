using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A usage map past its first window (qcatco/exportmdb.interop#20), as Access and Java Jackcess handle one
/// (UsageMap.InlineHandler and ReferenceHandler): an inline map moves its window when every page it holds still
/// fits in one, and otherwise becomes a reference map, whose map pages - each covering (page size - 4) x 8 pages -
/// are allocated as they are first needed. The rows here are 69 bytes, as Access writes them: 64 bitmap bytes, 512
/// pages a window, and as a reference map 17 map-page pointers.
/// </summary>
public sealed class UsageMapTests : IDisposable
{
    private const int PagesPerMapPage = (4096 - 4) * 8;   // 32,736

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"umap_{Guid.NewGuid():N}.mdb");
    private readonly PageFile _file;
    private readonly PageAllocator _allocator;
    private readonly int _umapPage;

    public UsageMapTests()
    {
        _file = new PageFile(_path, JetFormat.Jet4, FileMode.Create);
        _allocator = new PageAllocator(_file);
        _allocator.AllocatePage();
        _umapPage = _allocator.AllocatePage();
        _file.WritePage(_umapPage, UsageMap.CreateUmapPage(_file.Format, bitmapSize: 64));
    }

    public void Dispose()
    {
        _file.Dispose();
        File.Delete(_path);
    }

    private void Add(params int[] pages)
    {
        foreach (int page in pages)
            UsageMap.AddPage(_file, _allocator, _umapPage, 0, page);
    }

    private List<int> Pages(int row = 0) =>
        UsageMap.GetOwnedPages(_file.ReadPage(_umapPage), row, _file.Format, _file);

    private int RowStart()
    {
        var page = _file.ReadPage(_umapPage);
        return UsageMap.GetRowStart(page, 0, _file.Format);
    }

    private byte MapType() => _file.ReadPage(_umapPage)[RowStart()];

    private int InlineStartPage() => ByteUtil.GetInt(_file.ReadPage(_umapPage), RowStart() + 1);

    [Fact]
    public void A_page_inside_the_window_is_set_in_place()
    {
        Add(7, 300, 511);

        Assert.Equal(new[] { 7, 300, 511 }, Pages());
        Assert.Equal(0, MapType());
        Assert.Equal(0, InlineStartPage());
    }

    [Fact]
    public void An_inline_map_moves_its_window_when_its_pages_still_fit_in_one()
    {
        Add(403, 700);   // 700 is past the first window, but 400..700 fits in one of 512 pages

        Assert.Equal(new[] { 403, 700 }, Pages());
        Assert.Equal(0, MapType());
        Assert.Equal(400, InlineStartPage());   // a window starts on a multiple of 8
    }

    [Fact]
    public void An_empty_inline_map_moves_its_window_to_its_first_page()
    {
        Add(5_003);

        Assert.Equal(new[] { 5_003 }, Pages());
        Assert.Equal(0, MapType());
        Assert.Equal(5_000, InlineStartPage());
    }

    [Fact]
    public void An_inline_map_whose_pages_outgrow_one_window_becomes_a_reference_map()
    {
        Add(5, 300, 511, 2_000);

        Assert.Equal(new[] { 5, 300, 511, 2_000 }, Pages());
        Assert.Equal(1, MapType());
    }

    [Fact]
    public void A_reference_map_takes_a_map_page_for_each_span_of_32736_pages_as_it_needs_one()
    {
        var pages = new[] { 3, 600, PagesPerMapPage - 1, PagesPerMapPage, 70_000, 100_000 };
        Add(pages);

        Assert.Equal(pages, Pages());
        var umap = _file.ReadPage(_umapPage);
        int row = RowStart();
        var mapPages = Enumerable.Range(0, 5).Select(i => ByteUtil.GetInt(umap, row + 1 + i * 4)).ToList();
        Assert.Equal(0, mapPages[4]);   // 100,000 is in the fourth span: no fifth map page
        Assert.Equal(4, mapPages.Take(4).Distinct().Count());
        Assert.All(mapPages.Take(4), m =>
        {
            var mapPage = _file.ReadPage(m);
            Assert.Equal(JetFormat.PageTypeUsageMap, mapPage[0]);
            Assert.Equal(0x01, mapPage[1]);
        });
    }

    [Fact]
    public void A_page_beyond_what_the_row_can_address_throws_rather_than_writing_anywhere()
    {
        Add(5, 2_000);   // a reference map now: 17 map-page pointers in its 69 bytes

        var ex = Assert.Throws<NotSupportedException>(() => Add(17 * PagesPerMapPage));
        Assert.Contains("usage map", ex.Message);
        Assert.Equal(new[] { 5, 2_000 }, Pages());
    }

    [Fact]
    public void Growing_one_map_leaves_the_other_map_on_its_page_alone()
    {
        UsageMap.AddPage(_file, _allocator, _umapPage, 1, 42);

        Add(5, 2_000);

        Assert.Equal(new[] { 42 }, Pages(row: 1));
        Assert.Equal(new[] { 5, 2_000 }, Pages());
    }

    [Fact]
    public void The_last_page_is_the_highest_the_map_holds_in_either_form()
    {
        Assert.Equal(-1, UsageMap.GetLastPage(_file, _umapPage, 0));

        Add(40, 9);
        Assert.Equal(40, UsageMap.GetLastPage(_file, _umapPage, 0));

        Add(50_000, 1_000);
        Assert.Equal(1, MapType());
        Assert.Equal(50_000, UsageMap.GetLastPage(_file, _umapPage, 0));
    }

    [Fact]
    public void A_page_already_in_the_map_is_added_once()
    {
        Add(5, 5, 2_000, 2_000);

        Assert.Equal(new[] { 5, 2_000 }, Pages());
    }
}
