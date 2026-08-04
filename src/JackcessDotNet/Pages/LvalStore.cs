using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes long-value (Memo / OLE) data to dedicated LVAL data pages,
/// splitting large values into a chain of chunks when they exceed one page's capacity.
///
/// Each chunk row stored on a LVAL page contains:
///   [0 .. chunkDataLen-1]   actual data bytes
///   [chunkDataLen .. +3]    4-byte chain suffix:
///                             0xFF × 4                              → last (or only) chunk
///                             [3-byte LE nextPage][1-byte nextRow]  → link to next chunk
///
/// The 9-byte OTHER_PAGE LvRef written into the parent row's variable-length area:
///   [0..3]  4-byte LE total data byte count
///   [4]     0x40  (OTHER_PAGE marker)
///   [5..7]  3-byte LE page number of the first chunk
///   [8]     1-byte row index of the first chunk on that page
/// </summary>
internal sealed class LvalWriter
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;
    private readonly LvalUmapRef   _umap;   // owned/free usage maps for this LVAL column
    private readonly JetFormat     _format;

    public LvalWriter(PageFile file, PageAllocator allocator, LvalUmapRef umap)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _umap      = umap;
        _format    = file.Format;
    }

    /// <summary>
    /// Writes <paramref name="data"/> to LVAL storage and returns the 12-byte
    /// LvRef (REAL Jet format, matching Java Jackcess / ACE):
    ///   [lengthWithFlags:4 LE]  type in top two bits:
    ///                             0x40000000 OTHER_PAGE  - single chunk row
    ///                             0x00000000 OTHER_PAGES - chunk chain
    ///   [firstRow:1][firstPage:3 LE][unknown:4 = 0]
    /// OTHER_PAGE chunk rows contain raw data. OTHER_PAGES chunk rows are
    /// [nextRow:1][nextPage:3][payload], nextPage == 0 terminating the chain.
    /// </summary>
    public byte[] Write(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        int maxChunkRowSize = _format.DataPageInitialFreeSpace - JetFormat.SizeRowEntry;
        int totalLen = data.Length;

        if (totalLen <= maxChunkRowSize)
        {
            // Single chunk - OTHER_PAGE. The chunk row is the raw data.
            (int page, int row) = WriteChunkRow(data);
            return BuildLvRef(totalLen, LvalTypeOtherPage, page, row);
        }

        // Chunk chain - OTHER_PAGES. Written in reverse so each chunk knows its
        // successor's address up front.
        int chunkCapacity = maxChunkRowSize - 4;
        int numChunks = (totalLen + chunkCapacity - 1) / chunkCapacity;
        int nextPage = 0, nextRow = 0;
        for (int i = numChunks - 1; i >= 0; i--)
        {
            int start = i * chunkCapacity;
            int len   = Math.Min(chunkCapacity, totalLen - start);
            var chunk = new byte[4 + len];
            chunk[0] = (byte)nextRow;
            chunk[1] = (byte) nextPage;
            chunk[2] = (byte)(nextPage >> 8);
            chunk[3] = (byte)(nextPage >> 16);
            Array.Copy(data, start, chunk, 4, len);
            (nextPage, nextRow) = WriteChunkRow(chunk);
        }
        return BuildLvRef(totalLen, LvalTypeOtherPages, nextPage, nextRow);
    }

    internal const int LvalTypeThisPage   = unchecked((int)0x80000000);
    internal const int LvalTypeOtherPage  = 0x40000000;
    internal const int LvalTypeOtherPages = 0x00000000;
    internal const int LvalLengthMask     = 0x3FFFFFFF;

    private static byte[] BuildLvRef(int totalLen, int typeFlags, int page, int row)
    {
        var lvRef = new byte[12];
        ByteUtil.PutInt(lvRef, 0, unchecked(totalLen | typeFlags));
        lvRef[4] = (byte)row;
        lvRef[5] = (byte) page;
        lvRef[6] = (byte)(page >> 8);
        lvRef[7] = (byte)(page >> 16);
        // bytes 8..11 unknown, left zero
        return lvRef;
    }

    // Appends one chunk row to an available LVAL data page; returns (pageNum, rowIndex).
    private (int page, int row) WriteChunkRow(byte[] chunk)
    {
        int needed = chunk.Length + JetFormat.SizeRowEntry;

        // Find an existing LVAL page with enough room.
        byte[] umapPage  = _file.ReadPage(_umap.OwnedPage);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, _umap.OwnedRow, _format, _file);
        int    lvalPage  = -1;

        foreach (int pn in ownedList)
        {
            byte[] dp   = _file.ReadPage(pn);
            short  free = ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);
            if (free >= needed) { lvalPage = pn; break; }
        }

        if (lvalPage < 0)
        {
            // No page has room — allocate a fresh LVAL data page.
            // tdefPageNumber = 0: LVAL pages are not owned by a user TDEF.
            lvalPage = _allocator.AllocateDataPage(0);
            umapPage = _file.ReadPage(_umap.OwnedPage);   // re-read after alloc
            UsageMap.AddPage(umapPage, _umap.OwnedRow, lvalPage, _format);
            _file.WritePage(_umap.OwnedPage, umapPage);
            // TODO: also track the page in the free-space umap (_umap.FreePage/
            // FreeRow) like real Access; readers follow direct LvRef pointers,
            // so omitting it costs only reuse efficiency, not correctness.
        }

        // Append the chunk row (rows are packed from the end of the page).
        byte[] page      = _file.ReadPage(lvalPage);
        int    rowCount  = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        int    cursor    = _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int    rowStart  = cursor - chunk.Length;

        Array.Copy(chunk, 0, page, rowStart, chunk.Length);

        ByteUtil.PutShort(page,
            _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry,
            (short)rowStart);
        ByteUtil.PutShort(page, _format.OffsetDataNumRows,   (short)(rowCount + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));

        _file.WritePage(lvalPage, page);
        return (lvalPage, rowCount);   // rowCount was the 0-based index before increment
    }

    private static byte[] BuildOtherPageLvRef(int totalLen, int page, int row)
    {
        var lvRef = new byte[9];
        ByteUtil.PutInt(lvRef, 0, totalLen);   // 4-byte LE total data length
        lvRef[4] = 0x40;                        // OTHER_PAGE marker
        lvRef[5] = (byte) page;
        lvRef[6] = (byte)(page >>  8);
        lvRef[7] = (byte)(page >> 16);
        lvRef[8] = (byte) row;
        return lvRef;
    }
}

/// <summary>
/// Reads long-value data from LVAL data pages by following the chunk chain forward.
/// </summary>
internal sealed class LvalReader
{
    private readonly PageFile  _file;
    private readonly JetFormat _format;

    public LvalReader(PageFile file)
    {
        _file   = file ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
    }

    /// <summary>
    /// Reconstructs the LVAL value starting at (<paramref name="lvalPage"/>,
    /// <paramref name="lvalRow"/>). For OTHER_PAGE (<paramref name="chained"/>
    /// false) the single chunk row IS the data; for OTHER_PAGES each chunk row
    /// is [nextRow:1][nextPage:3][payload] with nextPage == 0 ending the chain.
    /// </summary>
    public byte[] Read(int lvalPage, int lvalRow, int totalLen, bool chained)
    {
        var result = new byte[totalLen];
        if (totalLen == 0) return result;

        int written = 0;
        int curPage = lvalPage;
        int curRow  = lvalRow;

        while (written < totalLen)
        {
            byte[] page     = _file.ReadPage(curPage);
            int    rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

            if (curRow >= rowCount)
                throw new InvalidOperationException(
                    $"LVAL page {curPage} has {rowCount} rows; requested row {curRow}.");

            (int rowStart, int rowEnd) = GetRowBounds(page, curRow, rowCount);
            int rowLen = rowEnd - rowStart;

            if (!chained)
            {
                Array.Copy(page, rowStart, result, 0, Math.Min(rowLen, totalLen));
                break;
            }

            if (rowLen < 4)
                throw new InvalidOperationException(
                    $"LVAL chain row [{curPage},{curRow}] is only {rowLen} bytes (minimum 4 required).");

            int nextRow  = page[rowStart];
            int nextPage = page[rowStart + 1] | (page[rowStart + 2] << 8) | (page[rowStart + 3] << 16);
            int copyLen  = Math.Min(rowLen - 4, totalLen - written);
            Array.Copy(page, rowStart + 4, result, written, copyLen);
            written += copyLen;

            if (nextPage == 0) break;
            curPage = nextPage;
            curRow  = nextRow;
        }

        return result;
    }

    private (int start, int end) GetRowBounds(byte[] page, int rowIndex, int rowCount)
    {
        int slotOff  = _format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        return (rowStart, rowEnd);
    }
}

/// <summary>
/// Frees (marks as deleted) a chain of LVAL chunks, then compacts each affected LVAL page
/// by reclaiming space from any deleted tail rows.
///
/// Tail compaction: rows are packed from the end of the page (row 0 = highest address,
/// row rowCount-1 = lowest address = most recently inserted).  If the most recently
/// inserted row is deleted we can shrink rowCount and increase freeSpace, making that
/// space immediately available to the next write.  We repeat until the last row is live.
///
/// Because LvalWriter writes chunks in REVERSE order, chunk 0 (first in the chain) is
/// always the LAST row written to its LVAL page and therefore the most eligible for tail
/// compaction.  Following the forward chain (chunk 0 → 1 → … → last) and compacting each
/// page in turn fully reclaims the space when all chunks were the sole occupants of their
/// respective pages.
/// </summary>
internal sealed class LvalFree
{
    private readonly PageFile  _file;
    private readonly JetFormat _format;

    public LvalFree(PageFile file)
    {
        _file   = file ?? throw new ArgumentNullException(nameof(file));
        _format = file.Format;
    }

    /// <summary>
    /// Marks every chunk starting at (<paramref name="lvalPage"/>,
    /// <paramref name="lvalRow"/>) as deleted and tail-compacts each page.
    /// <paramref name="chained"/> selects OTHER_PAGES chain walking
    /// ([nextRow:1][nextPage:3] prefix, nextPage == 0 ends) vs a single
    /// OTHER_PAGE chunk.
    /// </summary>
    public void FreeChain(int lvalPage, int lvalRow, bool chained)
    {
        int curPage = lvalPage;
        int curRow  = lvalRow;

        while (true)
        {
            byte[] page     = _file.ReadPage(curPage);
            int    rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

            if (curRow >= rowCount) break;   // safety: malformed chain

            (int rowStart, int rowEnd) = GetRowBounds(page, curRow, rowCount);
            int rowLen = rowEnd - rowStart;

            // Read chain prefix BEFORE modifying the page.
            int  nextPage = 0, nextRow = 0;
            bool hasNext  = false;
            if (chained && rowLen >= 4)
            {
                nextRow  = page[rowStart];
                nextPage = page[rowStart + 1] | (page[rowStart + 2] << 8) | (page[rowStart + 3] << 16);
                hasNext  = nextPage != 0;
            }

            // Mark this chunk's slot as deleted.
            int slotOff = _format.OffsetDataRowTable + curRow * JetFormat.SizeRowEntry;
            ByteUtil.PutUShort(page, slotOff,
                (ushort)(ByteUtil.GetUShort(page, slotOff) | 0x8000u));

            TrimDeletedTail(page);

            _file.WritePage(curPage, page);

            if (!hasNext) break;
            curPage = nextPage;
            curRow  = nextRow;
        }
    }

    // Pops deleted rows from the high-slot-index end of the page (the "bottom" of the
    // data area), increasing freeSpace for each reclaimed row.
    private void TrimDeletedTail(byte[] page)
    {
        int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);

        while (rowCount > 0)
        {
            int    slotOff = _format.OffsetDataRowTable + (rowCount - 1) * JetFormat.SizeRowEntry;
            ushort slotVal = ByteUtil.GetUShort(page, slotOff);
            if ((slotVal & 0x8000) == 0) break;   // last row is live — stop

            // Row (rowCount-1):
            //   start = slotVal & 0x1FFF
            //   end   = slot[rowCount-2] (or pageSize for the only row)
            int rowStart = slotVal & JetFormat.RowOffsetMask;
            int rowEnd   = rowCount == 1
                ? _format.PageSize
                : (ByteUtil.GetUShort(page,
                       _format.OffsetDataRowTable + (rowCount - 2) * JetFormat.SizeRowEntry)
                   & JetFormat.RowOffsetMask);
            int rowLen = rowEnd - rowStart;

            rowCount--;
            ByteUtil.PutShort(page, _format.OffsetDataNumRows, (short)rowCount);

            short freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace,
                (short)(freeSpace + rowLen + JetFormat.SizeRowEntry));
        }
    }

    private (int start, int end) GetRowBounds(byte[] page, int rowIndex, int rowCount)
    {
        int slotOff  = _format.OffsetDataRowTable + rowIndex * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = rowIndex == 0
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowIndex - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        return (rowStart, rowEnd);
    }
}
