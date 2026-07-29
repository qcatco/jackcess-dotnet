using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Reads and writes inline usage-map rows stored inside a Usage-Map page (type 0x05).
///
/// Each inline-map row layout:
///   Byte 0     : MAP_TYPE  (0x00 = inline)
///   Bytes 1-4  : start-page number (int, little-endian) – pages are numbered relative to this
///   Bytes 5-N  : bitmap   (1 bit per page, starting from start-page)
///
/// The row is stored inside the page using the standard slot-array layout shared by all
/// Jet page types: slot N lives at byte offset  OffsetDataRowTable + N * SizeRowEntry
/// and its value is the absolute byte-position of that row's data inside the page.
/// </summary>
internal static class UsageMap
{
    private const byte MapTypeInline    = 0x00;
    private const byte MapTypeReference = 0x01;

    /// <summary>
    /// Offset at which a dedicated bitmap page's bitmap starts — Jackcess Java's
    /// OFFSET_USAGE_MAP_PAGE_DATA. Such a page is a bare bitmap from this byte on; it does
    /// not carry the slot array a usage-map *declaration* page has.
    /// </summary>
    private const int RefMapBitmapStart = 4;

    // ── Page initialization ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh Usage-Map page that holds two empty inline maps:
    ///   row 0 = owned-pages map
    ///   row 1 = free-space map
    /// </summary>
    /// <param name="rowCount">
    /// How many inline maps the page carries. A table needs two (owned pages, free space) and
    /// one more per index — Access keeps an index's usage map as a further row of the same
    /// page, and an index whose map reference is blank leaves Access unable to read the table.
    /// </param>
    public static byte[] CreateUmapPage(JetFormat format, int rowCount = 2)
    {
        if (rowCount < 2)
            throw new ArgumentOutOfRangeException(nameof(rowCount),
                "A usage-map page always carries at least the owned-pages and free-space maps.");

        int bitmapSize = format.UmapInlineBitmapSize;
        int rowDataSize = 1 + 4 + bitmapSize;   // MAP_TYPE + startPage + bitmap
        var page = new byte[format.PageSize];

        // Page header. This page holds the table's two inline map rows, and Access stores
        // those as rows of a **data** page (0x01) — 0x05 is only for the global usage map
        // and for the dedicated bitmap pages a reference map points at. Writing 0x05 here
        // makes Access follow the TDEF's usage-map reference to a page it cannot parse, so
        // it never resolves a single row: "Not a valid bookmark".
        page[0] = JetFormat.PageTypeData;
        page[1] = 0x01;
        // bytes 4-7 are 0 (no owning TDEF)

        // Rows are packed from the end of the page, so row 0 sits at the highest address:
        // row 0 = owned pages, row 1 = free space, row 2+ = one per index.
        int cursor    = format.PageSize;
        int lastStart = cursor;

        for (int row = 0; row < rowCount; row++)
        {
            cursor -= rowDataSize;
            lastStart = cursor;
            page[cursor] = MapTypeInline;   // start page 0, bitmap already zeroed
            ByteUtil.PutShort(page, format.OffsetDataRowTable + row * JetFormat.SizeRowEntry,
                              (short)cursor);
        }

        ByteUtil.PutShort(page, format.OffsetDataNumRows, (short)rowCount);
        int freeSpace = lastStart - format.OffsetDataRowTable - rowCount * JetFormat.SizeRowEntry;
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
            int maxPagesPerRefPage = PagesPerBitmapPage(format);
            int numRefPages = BitmapPagePointers(rowLen);
            var result = new List<int>();

            for (int i = 0; i < numRefPages; i++)
            {
                int refPageNum = ByteUtil.GetInt(page, rowStart + 1 + i * 4);
                if (refPageNum <= 0) continue;

                byte[] refPage = file.ReadPage(refPageNum);
                if (refPage[0] != JetFormat.PageTypeUsageMap)
                    throw new InvalidDataException(
                        $"Expected usage-map page (type 0x05) at page {refPageNum}, found 0x{refPage[0]:X2}.");

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

    // ── Geometry ──────────────────────────────────────────────────────────────

    /// <summary>Inline-map start pages are always a multiple of 8.</summary>
    private static int ToValidStartPage(int startPage) => startPage / 8 * 8;

    private static bool IsWithinWindow(int pageNumber, int startPage, int bitmapLen)
        => pageNumber >= startPage && pageNumber < startPage + bitmapLen * 8;

    private static void SetBit(byte[] page, int bitmapStart, int relativePage)
        => page[bitmapStart + relativePage / 8] |= (byte)(1 << (relativePage % 8));

    /// <summary>How many pages one dedicated bitmap page covers.</summary>
    private static int PagesPerBitmapPage(JetFormat format)
        => (format.PageSize - RefMapBitmapStart) * 8;

    /// <summary>How many 4-byte bitmap-page pointers a reference row of this length holds.</summary>
    private static int BitmapPagePointers(int rowLen) => (rowLen - 1) / 4;

    private static bool FitsReferenceRow(int pageNumber, int rowLen, JetFormat format)
        => pageNumber / PagesPerBitmapPage(format) < BitmapPagePointers(rowLen);

    // ── Adding a page ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reports whether <see cref="AddPage"/> would accept <paramref name="pageNumber"/>,
    /// without modifying anything, assuming the caller has a page store to hand (which
    /// the writers do). Call this before allocating a page: the allocator extends the
    /// file the moment it hands a page out, so a map that cannot record the page would
    /// otherwise strand it at the end of the file for good.
    /// </summary>
    public static bool CanAddPage(byte[] page, int mapRow, int pageNumber, JetFormat format)
        => CanAddPage(page, mapRow, pageNumber, format, out _);

    /// <inheritdoc cref="CanAddPage(byte[], int, int, JetFormat)"/>
    /// <param name="refusal">
    /// Why the page cannot be added — null when it can. Lets a caller report the same
    /// reason <see cref="AddPage"/> would have thrown, without having to call it.
    /// </param>
    public static bool CanAddPage(byte[] page, int mapRow, int pageNumber, JetFormat format,
                                  out string? refusal)
    {
        refusal = Refuse(page, mapRow, pageNumber, format, hasPageStore: true);
        return refusal is null;
    }

    /// <summary>
    /// The one place that decides whether a page can join a map, and says why not when it
    /// can't. <see cref="CanAddPage"/> asks it and <see cref="AddPage"/> asks it and turns
    /// any answer into the exception, so the two cannot drift apart. Returns null when the
    /// page can be added.
    /// </summary>
    private static string? Refuse(byte[] page, int mapRow, int pageNumber, JetFormat format,
                                  bool hasPageStore)
    {
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);

        if (rowLen < 5)      return "The usage-map row is too small to hold a map.";
        if (pageNumber < 0)  return $"Page number {pageNumber} is negative.";

        byte mapType = page[rowStart];

        if (mapType == MapTypeReference)
            return !FitsReferenceRow(pageNumber, rowLen, format) ? OutOfReach(pageNumber, rowLen, format)
                 : !hasPageStore                                 ? NeedsPageStore(pageNumber)
                 : null;

        if (mapType != MapTypeInline)
            return $"Unknown usage-map type 0x{mapType:X2}.";

        int bitmapLen = rowLen - 5;
        int startPage = ByteUtil.GetInt(page, rowStart + 1);

        if (IsWithinWindow(pageNumber, startPage, bitmapLen)) return null;
        if (FitsAfterSlide(page, rowStart, bitmapLen, startPage, pageNumber, out _, out var owned))
            return null;

        // Only promotion is left, and the pointer array has to reach the new page as
        // well as everything the map already holds.
        int highest = owned.Count == 0 ? pageNumber : Math.Max(owned[^1], pageNumber);
        return !FitsReferenceRow(highest, rowLen, format) ? OutOfReach(highest, rowLen, format)
             : !hasPageStore                             ? NeedsPageStore(pageNumber)
             : null;
    }

    private static string NeedsPageStore(int pageNumber)
        => $"Page {pageNumber} calls for a reference-style usage map, which allocates bitmap " +
           "pages of its own — pass a PageAllocator and PageFile so it can.";

    private static string OutOfReach(int pageNumber, int rowLen, JetFormat format)
        => $"Page {pageNumber} is past everything this usage map can address: a " +
           $"reference-style row of {rowLen} bytes holds {BitmapPagePointers(rowLen)} bitmap " +
           $"pages covering {PagesPerBitmapPage(format)} pages each.";

    /// <summary>
    /// Records <paramref name="pageNumber"/> in the map at <paramref name="mapRow"/> of
    /// <paramref name="page"/> (in-place — the caller writes the page back).
    /// <para>
    /// An inline bitmap only addresses <c>bitmapLen * 8</c> pages from its start page — as
    /// little as 512 pages (~2 MB) in Access-authored files — so a table in a bigger file
    /// inevitably needs pages outside that window. Following Jackcess Java's
    /// <c>InlineHandler.addOrRemovePageNumberOutsideRange</c>, the window first slides to
    /// cover the whole owned span; when the span itself outgrows one bitmap the map is
    /// promoted to reference-style, which needs <paramref name="allocator"/> and
    /// <paramref name="file"/> to allocate and write its bitmap pages.
    /// </para>
    /// </summary>
    public static void AddPage(byte[] page, int mapRow, int pageNumber, JetFormat format,
                               PageAllocator? allocator = null, PageFile? file = null)
    {
        int rowStart = GetRowStart(page, mapRow, format);
        int rowLen   = GetRowLength(page, mapRow, format);

        if (rowLen < 5)
            throw new InvalidOperationException("Usage-map row is too small.");
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber),
                $"Page number {pageNumber} is negative.");

        bool hasPageStore = allocator is not null && file is not null;
        if (Refuse(page, mapRow, pageNumber, format, hasPageStore) is string refusal)
            throw new NotSupportedException(refusal);

        // Past this point the add is known to be possible, so nothing below re-checks.
        if (page[rowStart] == MapTypeReference)
        {
            AddToReferenceMap(page, rowStart, pageNumber, format, allocator!, file!);
            return;
        }

        int bitmapLen = rowLen - 5;
        int startPage = ByteUtil.GetInt(page, rowStart + 1);

        if (IsWithinWindow(pageNumber, startPage, bitmapLen))
        {
            SetBit(page, rowStart + 5, pageNumber - startPage);
            return;
        }

        if (FitsAfterSlide(page, rowStart, bitmapLen, startPage, pageNumber,
                           out int newStartPage, out var ownedPages))
        {
            SlideWindowTo(page, rowStart, bitmapLen, newStartPage, ownedPages);
            SetBit(page, rowStart + 5, pageNumber - newStartPage);
            return;
        }

        PromoteToReferenceMap(page, rowStart, rowLen, ownedPages, pageNumber,
                              format, allocator!, file!);
    }

    /// <summary>
    /// Works out where the window would have to start to cover both the pages already in
    /// the map and <paramref name="pageNumber"/>, and whether one bitmap reaches that far.
    /// Also hands back the pages it read, so a caller that goes on to slide or promote
    /// doesn't scan the bitmap again.
    /// </summary>
    private static bool FitsAfterSlide(byte[] page, int rowStart, int bitmapLen,
                                       int startPage, int pageNumber,
                                       out int newStartPage, out List<int> ownedPages)
    {
        ownedPages = ReadBitmap(page, rowStart + 5, bitmapLen, startPage);

        int firstPage = ownedPages.Count == 0 ? pageNumber : Math.Min(ownedPages[0],  pageNumber);
        int lastPage  = ownedPages.Count == 0 ? pageNumber : Math.Max(ownedPages[^1], pageNumber);

        newStartPage = ToValidStartPage(firstPage);
        return lastPage - newStartPage < bitmapLen * 8;
    }

    /// <summary>
    /// Re-bases the window on <paramref name="newStartPage"/> and re-writes every page the
    /// map already held — Jackcess Java's <c>InlineHandler.moveToNewStartPage</c>.
    /// </summary>
    private static void SlideWindowTo(byte[] page, int rowStart, int bitmapLen,
                                      int newStartPage, List<int> ownedPages)
    {
        Array.Clear(page, rowStart + 5, bitmapLen);
        ByteUtil.PutInt(page, rowStart + 1, newStartPage);

        foreach (int ownedPage in ownedPages)
            SetBit(page, rowStart + 5, ownedPage - newStartPage);
    }

    /// <summary>
    /// Turns an inline map into a reference-style one in place — Jackcess Java's
    /// <c>promoteInlineHandlerToReferenceHandler</c>. The row keeps its slot and length;
    /// its start-page and bitmap bytes become an array of 4-byte pointers to dedicated
    /// bitmap pages. That lifts the map's reach from <c>bitmapLen * 8</c> pages to
    /// pointers × pages-per-bitmap-page — 556,512 pages for the 69-byte rows
    /// Access-authored files use, i.e. past Access's own 2 GB file limit.
    /// </summary>
    private static void PromoteToReferenceMap(
        byte[] page, int rowStart, int rowLen, List<int> ownedPages, int pageNumber,
        JetFormat format, PageAllocator allocator, PageFile file)
    {
        Array.Clear(page, rowStart + 1, rowLen - 1);   // drop the start page and bitmap
        page[rowStart] = MapTypeReference;

        foreach (int ownedPage in ownedPages)
            AddToReferenceMap(page, rowStart, ownedPage, format, allocator, file);

        AddToReferenceMap(page, rowStart, pageNumber, format, allocator, file);
    }

    /// <summary>
    /// Sets <paramref name="pageNumber"/>'s bit on the bitmap page covering it, allocating
    /// that page and storing its pointer in the row when it does not exist yet. The caller
    /// has already established that the row can address the page.
    /// </summary>
    private static void AddToReferenceMap(
        byte[] page, int rowStart, int pageNumber,
        JetFormat format, PageAllocator allocator, PageFile file)
    {
        int perBitmapPage = PagesPerBitmapPage(format);
        int pointerIndex  = pageNumber / perBitmapPage;
        int pointerOffset = rowStart + 1 + pointerIndex * 4;
        int bitmapPage    = ByteUtil.GetInt(page, pointerOffset);

        if (bitmapPage <= 0)   // 0 is the row's "no bitmap page yet" marker
        {
            bitmapPage = allocator.AllocateReferenceBitmapPage();
            if (bitmapPage <= 0)
                throw new InvalidOperationException(
                    $"A usage-map bitmap page cannot live at page {bitmapPage}: the row stores " +
                    "0 to mean 'not allocated yet', so such a pointer would read back as unset " +
                    "and the bitmap would be silently re-allocated over. Page 0 holds the " +
                    "database header in any real file, so this means the allocator was handed " +
                    "an empty one.");
            ByteUtil.PutInt(page, pointerOffset, bitmapPage);
        }

        byte[] bitmap = file.ReadPage(bitmapPage);
        SetBit(bitmap, RefMapBitmapStart, pageNumber - pointerIndex * perBitmapPage);
        file.WritePage(bitmapPage, bitmap);
    }
}
