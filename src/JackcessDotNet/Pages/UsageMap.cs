using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Reads and writes usage-map rows: the lists of pages a table, or a long-value column, owns.
///
/// A row comes in one of two forms, as in Access and Java Jackcess (UsageMap.InlineHandler and ReferenceHandler):
///   inline     Byte 0 = 0x00, bytes 1-4 = start page (int, LE), then a bitmap, 1 bit per page from the start
///              page. It covers one window of (row length - 5) x 8 pages: 512 in Access's own 69-byte rows,
///              1,600 in this engine's.
///   reference  Byte 0 = 0x01, then 4-byte LE page numbers of usage-map pages (type 0x05), the i-th covering pages
///              i x N .. (i + 1) x N - 1 with N = (page size - 4) x 8, its bitmap from byte 4; 0 where none is
///              needed yet. 17 pointers in a 69-byte row reach past Jet's 2 GB.
///
/// A page outside an inline map's window moves the window when every page the map holds, and the new one, fit in
/// one; otherwise the map becomes a reference map. So a file can grow to Jet's limit.
///
/// The row is stored inside the page using the standard slot-array layout shared by all
/// Jet page types: slot N lives at byte offset  OffsetDataRowTable + N * SizeRowEntry
/// and its value is the absolute byte-position of that row's data inside the page.
/// </summary>
internal static class UsageMap
{
    private const byte MapTypeInline    = 0x00;
    private const byte MapTypeReference = 0x01;

    /// <summary>Where a reference map page's bitmap starts: after its 4-byte header.</summary>
    private const int RefMapBitmapStart = 4;

    // ── Page initialization ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh Usage-Map page that holds two empty inline maps:
    ///   row 0 = owned-pages map
    ///   row 1 = free-space map
    /// </summary>
    public static byte[] CreateUmapPage(JetFormat format)
        => CreateUmapPage(format, format.UmapInlineBitmapSize);

    /// <summary>
    /// As <see cref="CreateUmapPage(JetFormat)"/>, with inline bitmaps of <paramref name="bitmapSize"/> bytes:
    /// Access's own are 64.
    /// </summary>
    public static byte[] CreateUmapPage(JetFormat format, int bitmapSize)
    {
        int rowDataSize = 1 + 4 + bitmapSize;   // MAP_TYPE + startPage + bitmap
        var page = new byte[format.PageSize];

        // Page header
        page[0] = JetFormat.PageTypeUsageMap;
        page[1] = 0x01;
        // bytes 4-7 are 0 (no owning TDEF)

        // Write two rows, packed from the end of the page
        int cursor = format.PageSize;

        // Row 0 (owned pages) – written first = at higher address
        cursor -= rowDataSize;
        int row0Start = cursor;
        page[row0Start] = MapTypeInline;   // MAP_TYPE
        // start-page = 0, bitmap = all zeros (already zeroed)

        // Row 1 (free-space pages) – written second = at lower address
        cursor -= rowDataSize;
        int row1Start = cursor;
        page[row1Start] = MapTypeInline;

        // Slot table at OffsetDataRowTable (Jet3=10, Jet4=14)
        ByteUtil.PutShort(page, format.OffsetDataRowTable,                   (short)row0Start);
        ByteUtil.PutShort(page, format.OffsetDataRowTable + JetFormat.SizeRowEntry, (short)row1Start);

        // Row count and free space
        ByteUtil.PutShort(page, format.OffsetDataNumRows, 2);
        int freeSpace = row1Start - format.OffsetDataRowTable - 2 * JetFormat.SizeRowEntry;
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)freeSpace);

        return page;
    }

    // ── Row reading helpers ───────────────────────────────────────────────────

    /// <summary>Returns the byte-offset within <paramref name="page"/> at which row
    /// <paramref name="rowNum"/> starts, after stripping DELETED/OVERFLOW flag bits.</summary>
    public static int GetRowStart(byte[] page, int rowNum, JetFormat format)
        => ByteUtil.GetUShort(page, format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry)
           & JetFormat.RowOffsetMask;

    /// <summary>Returns the length in bytes of row <paramref name="rowNum"/> in
    /// <paramref name="page"/>, using the slot-array convention that rows are packed
    /// from the end of the page (row 0 is last = highest address).</summary>
    public static int GetRowLength(byte[] page, int rowNum, JetFormat format)
    {
        int rowStart = GetRowStart(page, rowNum, format);
        int end = (rowNum == 0) ? format.PageSize
                                : GetRowStart(page, rowNum - 1, format);
        return end - rowStart;
    }

    // ── Bitmap operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of page numbers whose bits are set in the inline map stored
    /// at <paramref name="mapRow"/> of <paramref name="page"/>.
    /// Inline-only — throws for reference-style maps (use the overload that takes a PageFile).
    /// </summary>
    public static List<int> GetOwnedPages(byte[] page, int mapRow, JetFormat format)
        => GetOwnedPages(page, mapRow, format, file: null);

    /// <summary>
    /// Returns the list of page numbers covered by the usage map at <paramref name="mapRow"/>.
    /// Resolves both inline (single bitmap) and reference (list-of-bitmap-page-pointers) styles.
    /// </summary>
    public static List<int> GetOwnedPages(byte[] page, int mapRow, JetFormat format, PageFile? file)
    {
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);

        if (rowLen < 1) return new List<int>();

        byte mapType = page[rowStart];

        if (mapType == MapTypeInline)
        {
            if (rowLen < 5) return new List<int>();
            int startPage = ByteUtil.GetInt(page, rowStart + 1);
            return ReadBitmap(page, rowStart + 5, rowLen - 5, startPage);
        }

        if (mapType == MapTypeReference)
        {
            if (file is null)
                throw new InvalidOperationException(
                    "Reference-style usage map encountered but no PageFile was provided to resolve it.");

            // Row layout: 0x01 followed by an array of 4-byte LE page numbers.
            // Each pointer references a dedicated UsageMap page (type 0x05) whose
            // bitmap starts at byte 4 (OFFSET_USAGE_MAP_PAGE_DATA).
            int maxPagesPerRefPage = PagesPerMapPage(format);
            int numRefPages = (rowLen - 1) / 4;
            var result = new List<int>();

            for (int i = 0; i < numRefPages; i++)
            {
                int refPageNum = ByteUtil.GetInt(page, rowStart + 1 + i * 4);
                if (refPageNum <= 0) continue;

                byte[] refPage = ReadMapPage(file, refPageNum);
                int bitmapLen = format.PageSize - RefMapBitmapStart;
                result.AddRange(ReadBitmap(refPage, RefMapBitmapStart, bitmapLen, i * maxPagesPerRefPage));
            }
            return result;
        }

        throw new NotSupportedException($"Unknown usage-map type 0x{mapType:X2}.");
    }

    private static List<int> ReadBitmap(byte[] page, int bitmapStart, int bitmapLen, int basePage)
    {
        var result = new List<int>();
        for (int byteIdx = 0; byteIdx < bitmapLen; byteIdx++)
        {
            byte b = page[bitmapStart + byteIdx];
            if (b == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) != 0)
                    result.Add(basePage + byteIdx * 8 + bit);
            }
        }
        return result;
    }

    /// <summary>The highest page whose bit is set in a bitmap, or -1.</summary>
    private static int LastInBitmap(byte[] page, int bitmapStart, int bitmapLen, int basePage)
    {
        for (int byteIdx = bitmapLen - 1; byteIdx >= 0; byteIdx--)
        {
            byte b = page[bitmapStart + byteIdx];
            if (b == 0) continue;
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((b & (1 << bit)) != 0)
                    return basePage + byteIdx * 8 + bit;
            }
        }
        return -1;
    }

    private static void SetBit(byte[] page, int bitmapStart, int relativePage)
        => page[bitmapStart + relativePage / 8] |= (byte)(1 << (relativePage % 8));

    /// <summary>The pages one reference map page covers: (page size - 4) x 8, 32,736 for Jet4.</summary>
    private static int PagesPerMapPage(JetFormat format) => (format.PageSize - RefMapBitmapStart) * 8;

    private static byte[] ReadMapPage(PageFile file, int pageNumber)
    {
        byte[] page = file.ReadPage(pageNumber);
        if (page[0] != JetFormat.PageTypeUsageMap)
            throw new InvalidDataException(
                $"Expected usage-map page (type 0x05) at page {pageNumber}, found 0x{page[0]:X2}.");
        return page;
    }

    /// <summary>
    /// Whether <paramref name="pageNumber"/> is in the usage map at <paramref name="mapRow"/> of page
    /// <paramref name="umapPageNumber"/>.
    /// </summary>
    public static bool Contains(PageFile file, int umapPageNumber, int mapRow, int pageNumber)
    {
        var format = file.Format;
        byte[] page = file.ReadPage(umapPageNumber);
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);
        if (rowLen < 1 || pageNumber < 0) return false;

        byte mapType = page[rowStart];
        if (mapType == MapTypeInline)
        {
            if (rowLen < 5) return false;
            int relative = pageNumber - ByteUtil.GetInt(page, rowStart + 1);
            return relative >= 0 && relative < (rowLen - 5) * 8 && IsSet(page, rowStart + 5, relative);
        }

        if (mapType == MapTypeReference)
        {
            int perMapPage = PagesPerMapPage(format);
            int index = pageNumber / perMapPage;
            if (index >= (rowLen - 1) / 4) return false;
            int mapPageNumber = ByteUtil.GetInt(page, rowStart + 1 + index * 4);
            return mapPageNumber > 0
                && IsSet(ReadMapPage(file, mapPageNumber), RefMapBitmapStart, pageNumber - index * perMapPage);
        }

        throw new NotSupportedException($"Unknown usage-map type 0x{mapType:X2}.");
    }

    private static bool IsSet(byte[] page, int bitmapStart, int relativePage)
        => (page[bitmapStart + relativePage / 8] & (1 << (relativePage % 8))) != 0;

    /// <summary>
    /// The highest page number in the usage map at <paramref name="mapRow"/> of page
    /// <paramref name="umapPageNumber"/>, or -1 when it holds none: the page a table's next row, or a column's next
    /// long value, goes to first. Reads the usage-map page and, for a reference map, the one map page that holds it.
    /// </summary>
    public static int GetLastPage(PageFile file, int umapPageNumber, int mapRow)
    {
        var format = file.Format;
        byte[] page = file.ReadPage(umapPageNumber);
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);
        if (rowLen < 1) return -1;

        byte mapType = page[rowStart];
        if (mapType == MapTypeInline)
        {
            if (rowLen < 5) return -1;
            return LastInBitmap(page, rowStart + 5, rowLen - 5, ByteUtil.GetInt(page, rowStart + 1));
        }

        if (mapType == MapTypeReference)
        {
            int perMapPage = PagesPerMapPage(format);
            for (int i = (rowLen - 1) / 4 - 1; i >= 0; i--)
            {
                int mapPageNumber = ByteUtil.GetInt(page, rowStart + 1 + i * 4);
                if (mapPageNumber <= 0) continue;
                byte[] mapPage = ReadMapPage(file, mapPageNumber);
                int last = LastInBitmap(mapPage, RefMapBitmapStart, format.PageSize - RefMapBitmapStart, i * perMapPage);
                if (last >= 0) return last;
            }
            return -1;
        }

        throw new NotSupportedException($"Unknown usage-map type 0x{mapType:X2}.");
    }

    /// <summary>
    /// Adds <paramref name="pageNumber"/> to the usage map at <paramref name="mapRow"/> of page
    /// <paramref name="umapPageNumber"/>, and writes what changed. A page inside an inline map's window is set in
    /// place; one outside it moves the window if every page the map holds, and the new one, still fit in one window
    /// (starting on a multiple of 8, as Jackcess's toValidStartPage has it); otherwise the map becomes a reference
    /// map and keeps every page it held. A reference map allocates a map page when it first needs one.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The page is beyond what the row can address as a reference map - past Jet's 2 GB in Access's rows.
    /// </exception>
    public static void AddPage(PageFile file, PageAllocator allocator, int umapPageNumber, int mapRow, int pageNumber)
    {
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        var format = file.Format;
        byte[] page = file.ReadPage(umapPageNumber);
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);
        if (rowLen < 5)
            throw new InvalidOperationException("Usage-map row is too small.");

        byte mapType = page[rowStart];
        if (mapType == MapTypeReference)
        {
            AddToReferenceMap(file, allocator, page, umapPageNumber, rowStart, rowLen, new[] { pageNumber }, rowChanged: false);
            return;
        }
        if (mapType != MapTypeInline)
            throw new NotSupportedException($"Unknown usage-map type 0x{mapType:X2}.");

        int startPage = ByteUtil.GetInt(page, rowStart + 1);
        int capacity  = (rowLen - 5) * 8;
        if (pageNumber >= startPage && pageNumber - startPage < capacity)
        {
            SetBit(page, rowStart + 5, pageNumber - startPage);
            file.WritePage(umapPageNumber, page);
            return;
        }

        var pages = ReadBitmap(page, rowStart + 5, rowLen - 5, startPage);
        pages.Add(pageNumber);
        int first = pages.Min();
        int last  = pages.Max();
        int newStart = first - first % 8;
        Array.Clear(page, rowStart + 1, rowLen - 1);

        // Jackcess's own test: the window must hold the span with a page to spare.
        if (last - newStart + 1 < capacity)
        {
            ByteUtil.PutInt(page, rowStart + 1, newStart);
            foreach (int p in pages)
                SetBit(page, rowStart + 5, p - newStart);
            file.WritePage(umapPageNumber, page);
            return;
        }

        if (last / PagesPerMapPage(format) >= (rowLen - 1) / 4)
            throw BeyondTheMap(last, rowLen, format);

        // A reference map with no map pages yet, then every page the inline map held and the new one. The row is
        // written as a reference map only once its map pages are: until then the inline map on disk still holds
        // every page but the new one.
        page[rowStart] = MapTypeReference;
        AddToReferenceMap(file, allocator, page, umapPageNumber, rowStart, rowLen, pages, rowChanged: true);
    }

    /// <summary>
    /// Sets <paramref name="pageNumbers"/> in the reference map whose row is at <paramref name="rowStart"/> of
    /// <paramref name="umapPage"/>, allocating map pages as needed. Writes each map page it touched once, then the
    /// usage-map page when a pointer changed or <paramref name="rowChanged"/> - the row just became a reference map.
    /// </summary>
    private static void AddToReferenceMap(PageFile file, PageAllocator allocator, byte[] umapPage, int umapPageNumber,
                                          int rowStart, int rowLen, IEnumerable<int> pageNumbers, bool rowChanged)
    {
        var format = file.Format;
        int perMapPage = PagesPerMapPage(format);
        int pointers = (rowLen - 1) / 4;
        var touched = new Dictionary<int, byte[]>();
        bool umapChanged = rowChanged;

        foreach (int pageNumber in pageNumbers)
        {
            int index = pageNumber / perMapPage;
            if (index >= pointers)
                throw BeyondTheMap(pageNumber, rowLen, format);

            int pointerOffset = rowStart + 1 + index * 4;
            int mapPageNumber = ByteUtil.GetInt(umapPage, pointerOffset);
            if (!touched.TryGetValue(mapPageNumber, out byte[]? mapPage))
            {
                if (mapPageNumber <= 0)
                {
                    mapPageNumber = allocator.AllocatePage();
                    mapPage = NewMapPage(format);
                    ByteUtil.PutInt(umapPage, pointerOffset, mapPageNumber);
                    umapChanged = true;
                }
                else
                {
                    mapPage = ReadMapPage(file, mapPageNumber);
                }
                touched[mapPageNumber] = mapPage;
            }
            SetBit(mapPage, RefMapBitmapStart, pageNumber - index * perMapPage);
        }

        foreach (var kv in touched)
            file.WritePage(kv.Key, kv.Value);
        if (umapChanged)
            file.WritePage(umapPageNumber, umapPage);
    }

    /// <summary>A reference map's page: type 0x05, then 0x01 and two zero bytes, as Access and Jackcess write one.</summary>
    private static byte[] NewMapPage(JetFormat format)
    {
        var page = new byte[format.PageSize];
        page[0] = JetFormat.PageTypeUsageMap;
        page[1] = 0x01;
        return page;
    }

    private static NotSupportedException BeyondTheMap(int pageNumber, int rowLen, JetFormat format)
        => new NotSupportedException(
            $"Page {pageNumber} is beyond what this usage map can address: its {rowLen}-byte row holds " +
            $"{(rowLen - 1) / 4} map pages of {PagesPerMapPage(format)} pages each.");
}
