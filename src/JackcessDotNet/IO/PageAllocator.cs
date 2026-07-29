using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>Allocates new pages by appending them to the file.</summary>
public sealed class PageAllocator
{
    private readonly PageFile _file;

    public PageAllocator(PageFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>
    /// The page number <see cref="AllocatePage"/> will hand out next. Allocating
    /// extends the file immediately, so a caller that has to record the page
    /// somewhere else (a usage map) should check it can do so against this number
    /// first — a page that gets allocated but not recorded is stranded for good.
    /// </summary>
    public int NextPageNumber => _file.PageCount;

    /// <summary>Appends a blank page and returns its page number.</summary>
    public int AllocatePage()
    {
        int pageNumber = _file.PageCount;
        _file.WritePage(pageNumber, new byte[_file.Format.PageSize]);
        return pageNumber;
    }

    /// <summary>
    /// Allocates a new data page (type 0x01), writes the standard 14-byte header,
    /// and returns its page number.  The caller is responsible for adding the page
    /// to the owning table's usage-map.
    /// </summary>
    public int AllocateDataPage(int tdefPageNumber)
    {
        var format     = _file.Format;
        int pageNumber = AllocatePage();
        var page       = new byte[format.PageSize];

        page[0] = JetFormat.PageTypeData;
        page[1] = 0x01;
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)format.DataPageInitialFreeSpace);
        ByteUtil.PutInt  (page, JetFormat.OffsetDataTdefPage,  tdefPageNumber);
        // numRows = 0 (already zero), at format.OffsetDataNumRows (Jet3=8, Jet4=12)

        _file.WritePage(pageNumber, page);
        return pageNumber;
    }

    /// <summary>
    /// Allocates a page holding one bitmap of a reference-style usage map: a type-0x05
    /// page whose bitmap runs from byte 4 to the end. Note this is *not* the layout
    /// <see cref="AllocateUmapPage"/> writes — that one is the map *declaration* page,
    /// with a slot array and two inline map rows.
    /// </summary>
    public int AllocateReferenceBitmapPage()
    {
        var format     = _file.Format;
        int pageNumber = AllocatePage();
        var page       = new byte[format.PageSize];

        page[0] = JetFormat.PageTypeUsageMap;
        page[1] = 0x01;
        // bytes 2-3 unused; the bitmap starts at byte 4 and is all zeros

        _file.WritePage(pageNumber, page);
        return pageNumber;
    }

    /// <summary>
    /// Allocates a new Usage-Map page (type 0x05) with two empty inline maps
    /// (owned-pages map at row 0, free-space map at row 1), and returns its page number.
    /// </summary>
    /// <param name="rowCount">
    /// Inline maps to carry: two for the table (owned pages, free space) plus one per index.
    /// </param>
    public int AllocateUmapPage(int rowCount = 2)
    {
        var format     = _file.Format;
        int pageNumber = AllocatePage();
        _file.WritePage(pageNumber, UsageMap.CreateUmapPage(format, rowCount));
        return pageNumber;
    }
}
