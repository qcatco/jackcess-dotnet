using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// The database's global usage map (page 1, row 0): the pages Access may hand
/// out, a set bit for each free page. Access allocates from it, so a page this
/// library appends has to leave it, or Access later hands that page out again
/// and writes over what is on it.
///
/// Kept the way Jackcess keeps it. An inline map counts every page outside its
/// window as free (Access's own maps cover a file's pages and the pages it is
/// about to add), so when a page is appended outside the window the map becomes
/// a reference map, as Access's do for larger files: its map pages start with
/// every page free, and every page up to the one appended is marked used.
/// </summary>
internal static class GlobalUsageMap
{
    private const int  MapPageNumber     = 1;
    private const int  MapRow            = 0;
    private const byte MapTypeInline     = 0x00;
    private const byte MapTypeReference  = 0x01;
    private const int  RefMapBitmapStart = 4;

    /// <summary>Takes <paramref name="pageNumber"/>, which was just appended to the file, out of the free pages.</summary>
    public static void MarkUsed(PageFile file, int pageNumber)
    {
        var format = file.Format;
        if (file.PageCount <= MapPageNumber || !IsDatabase(file)) return;

        byte[] page = file.ReadPage(MapPageNumber);
        if (page[0] != JetFormat.PageTypeData || ByteUtil.GetShort(page, format.OffsetDataNumRows) <= MapRow)
            return;   // no global map to keep
        int rowStart = UsageMap.GetRowStart(page, MapRow, format);
        int rowLen   = UsageMap.GetRowLength(page, MapRow, format);
        if (rowLen < 5) return;

        switch (page[rowStart])
        {
            case MapTypeInline:
            {
                int start    = ByteUtil.GetInt(page, rowStart + 1);
                int capacity = (rowLen - 5) * 8;
                if (pageNumber >= start && pageNumber - start < capacity)
                {
                    ClearBit(page, rowStart + 5, pageNumber - start);
                    file.WritePage(MapPageNumber, page);
                }
                else if (pageNumber >= start)
                {
                    ToReferenceMap(file, page, rowStart, rowLen, pageNumber);
                }
                return;
            }
            case MapTypeReference:
                MarkUsedInReferenceMap(file, page, rowStart, rowLen, pageNumber);
                return;
            default:
                throw new NotSupportedException($"The global usage map has an unknown type, 0x{page[rowStart]:X2}.");
        }
    }

    /// <summary>
    /// Rewrites the inline map at <paramref name="rowStart"/> as a reference map in which
    /// every page up to <paramref name="frontier"/>, and every map page it takes, is used.
    /// Holes the inline map held inside the file are given up, as Jackcess does.
    /// </summary>
    private static void ToReferenceMap(PageFile file, byte[] mapRowPage, int rowStart, int rowLen, int frontier)
    {
        var format     = file.Format;
        int perMapPage = PagesPerMapPage(format);
        int pointers   = (rowLen - 1) / 4;

        Array.Clear(mapRowPage, rowStart, rowLen);
        mapRowPage[rowStart] = MapTypeReference;

        var mapPages = new Dictionary<int, (int Number, byte[] Bytes)>();
        int last = frontier;
        for (int p = 0; p <= last; p++)   // last grows as map pages are appended after the frontier
        {
            int index = p / perMapPage;
            if (!mapPages.TryGetValue(index, out var map))
            {
                map = AppendMapPage(file, mapRowPage, rowStart, index, pointers);
                mapPages[index] = map;
                last = Math.Max(last, map.Number);
            }
            ClearBit(map.Bytes, RefMapBitmapStart, p - index * perMapPage);
        }

        foreach (var map in mapPages.Values)
            file.WritePage(map.Number, map.Bytes);
        file.WritePage(MapPageNumber, mapRowPage);
    }

    private static void MarkUsedInReferenceMap(PageFile file, byte[] mapRowPage, int rowStart, int rowLen, int pageNumber)
    {
        var format     = file.Format;
        int perMapPage = PagesPerMapPage(format);
        int pointers   = (rowLen - 1) / 4;

        // A new map page is appended too, so it can need marking (in its own map page or the next).
        var pending = new Queue<int>();
        pending.Enqueue(pageNumber);
        var touched = new Dictionary<int, (int Number, byte[] Bytes)>();
        bool rowChanged = false;
        while (pending.Count > 0)
        {
            int p = pending.Dequeue();
            int index = p / perMapPage;
            if (!touched.TryGetValue(index, out var map))
            {
                int number = index < pointers ? ByteUtil.GetInt(mapRowPage, rowStart + 1 + index * 4) : 0;
                if (number > 0)
                {
                    map = (number, file.ReadPage(number));
                    if (map.Bytes[0] != JetFormat.PageTypeUsageMap)
                        throw new InvalidDataException($"The global usage map's page {number} is not a usage-map page.");
                }
                else
                {
                    map = AppendMapPage(file, mapRowPage, rowStart, index, pointers);
                    rowChanged = true;
                    pending.Enqueue(map.Number);
                }
                touched[index] = map;
            }
            ClearBit(map.Bytes, RefMapBitmapStart, p - index * perMapPage);
        }

        foreach (var map in touched.Values)
            file.WritePage(map.Number, map.Bytes);
        if (rowChanged)
            file.WritePage(MapPageNumber, mapRowPage);
    }

    /// <summary>
    /// Appends a map page for the pages of <paramref name="index"/>, every one of them free,
    /// and points the row at it. The page is written now, so the file's length counts it.
    /// </summary>
    private static (int Number, byte[] Bytes) AppendMapPage(PageFile file, byte[] mapRowPage, int rowStart, int index, int pointers)
    {
        if (index >= pointers)
            throw new NotSupportedException(
                $"The global usage map holds {pointers} map pages; the file has grown past what it can address.");

        var format = file.Format;
        var bytes  = new byte[format.PageSize];
        bytes[0] = JetFormat.PageTypeUsageMap;
        bytes[1] = 0x01;
        for (int i = RefMapBitmapStart; i < bytes.Length; i++) bytes[i] = 0xFF;

        int number = file.PageCount;
        file.WritePage(number, bytes);
        ByteUtil.PutInt(mapRowPage, rowStart + 1 + index * 4, number);
        return (number, bytes);
    }

    private static int PagesPerMapPage(JetFormat format) => (format.PageSize - RefMapBitmapStart) * 8;

    // Page 0 of a database is its header: "Standard Jet DB" or "Standard ACE DB" from byte 4. A bare page
    // file (as some tests build) has no global map to keep.
    private static bool IsDatabase(PageFile file)
    {
        byte[] header = file.ReadPage(0);
        const string signature = "Standard ";
        for (int i = 0; i < signature.Length; i++)
            if (header[4 + i] != (byte)signature[i]) return false;
        return true;
    }

    private static void ClearBit(byte[] page, int bitmapStart, int relativePage)
        => page[bitmapStart + relativePage / 8] &= (byte)~(1 << (relativePage % 8));
}
