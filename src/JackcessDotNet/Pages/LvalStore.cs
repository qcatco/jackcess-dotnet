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

        // Access's own chunk rows are PageSize - 20 bytes (4076 for Jet4) -
        // matches Java Jackcess MAX_LONG_VALUE_ROW_SIZE. Larger rows make ACE
        // fail to materialize the value.
        int maxChunkRowSize = _format.PageSize - 20;
        int totalLen = data.Length;

        if (totalLen <= maxChunkRowSize)
        {
            // Single chunk - OTHER_PAGE. The chunk row is the raw data.
            var single = new PagePicker(this);
            (int page, _) = single.Reserve(totalLen + JetFormat.SizeRowEntry);
            int row = WriteChunkRowOnPage(page, data);
            single.Done();
            return BuildLvRef(totalLen, LvalTypeOtherPage, page, row);
        }

        // Chunk chain - OTHER_PAGES, laid out the way Access does it: chunks in
        // FORWARD order. Placement is planned up front (so each chunk can embed
        // its successor's address) by a PagePicker.
        int chunkCapacity = maxChunkRowSize - 4;
        int numChunks = (totalLen + chunkCapacity - 1) / chunkCapacity;

        var placements = new (int page, int row)[numChunks];
        var picker = new PagePicker(this);
        for (int i = 0; i < numChunks; i++)
        {
            int len = Math.Min(chunkCapacity, totalLen - i * chunkCapacity);
            placements[i] = picker.Reserve(4 + len + JetFormat.SizeRowEntry);
        }

        for (int i = 0; i < numChunks; i++)
        {
            int start = i * chunkCapacity;
            int len   = Math.Min(chunkCapacity, totalLen - start);
            (int nextPage, int nextRow) = i + 1 < numChunks ? placements[i + 1] : (0, 0);

            var chunk = new byte[4 + len];
            chunk[0] = (byte)nextRow;
            chunk[1] = (byte) nextPage;
            chunk[2] = (byte)(nextPage >> 8);
            chunk[3] = (byte)(nextPage >> 16);
            Array.Copy(data, start, chunk, 4, len);
            // Each chunk names the next by the row it was planned to take: a chunk landing elsewhere would leave the
            // chain pointing at the wrong row, so that is an error, not a value read back wrong later.
            int row = WriteChunkRowOnPage(placements[i].page, chunk);
            if (row != placements[i].row)
                throw new InvalidOperationException(
                    $"LVAL chunk {i} took row {row} of page {placements[i].page}, not the row {placements[i].row} its chain names.");
        }
        picker.Done();
        return BuildLvRef(totalLen, LvalTypeOtherPages, placements[0].page, placements[0].row);
    }

    /// <summary>
    /// Chooses the LVAL pages a value's chunk rows go to, and the row each takes there: pages of this column whose
    /// space a deleted value freed earlier in the session (<see cref="PageFile.FreedLvalPages"/>), then the column's
    /// last page, and a new page when none has room. A freed page is read only when it was last seen with room for the
    /// chunk, at most once a value, and the room left on the pages the column has noted is noted again when the value
    /// is written. Looking through every
    /// page the column owned for room read the whole column on every write, so each write was slower than the last;
    /// the freed pages keep what that bought - a deleted value's space is used again.
    /// </summary>
    private sealed class PagePicker
    {
        private readonly LvalWriter _writer;
        private IEnumerator<KeyValuePair<int, int>>? _freed;   // this column's freed pages, lowest first, and their room
        private int _freedWalkedFor = int.MaxValue;            // the smallest chunk they have been walked for
        private readonly HashSet<int> _freedRead = new();      // the freed pages read
        private readonly List<int> _open = new();              // pages read or allocated, with room left
        private readonly Dictionary<int, int> _free = new();
        private readonly Dictionary<int, int> _rows = new();
        private bool _lastPageRead;

        public PagePicker(LvalWriter writer)
        {
            _writer = writer;
        }

        /// <summary>A page with room for a chunk row of <paramref name="needed"/> bytes, its slot included, and the
        /// index the row takes there - allocating the page when none of the candidates has room.</summary>
        public (int page, int row) Reserve(int needed)
        {
            int target = OpenPageWithRoom(needed);
            if (target < 0) target = FreedPageWithRoom(needed);
            if (target < 0) target = LastPageWithRoom(needed);
            if (target < 0) target = NewPage();

            int row = _rows[target];
            _rows[target] = row + 1;
            _free[target] -= needed;
            if (_free[target] < FreedLvalPages.MinUsefulFreeSpace)
                _open.Remove(target);
            return (target, row);
        }

        private bool HasRoom(int page, int needed)
            => _free[page] >= needed && _rows[page] < DataPageWriter.MaxRowsPerPage;

        private int OpenPageWithRoom(int needed)
        {
            foreach (int page in _open)
            {
                if (HasRoom(page, needed))
                    return page;
            }
            return -1;
        }

        // A value's chunks never grow - full ones, then the last - so the freed pages are walked once for the full
        // chunks, and again for a last chunk smaller than them: a page passed over as too small for a full chunk may take
        // the last. Nothing is noted while a value is planned, so a walk sees the records as the first one did, and a
        // page already read is not read again.
        private int FreedPageWithRoom(int needed)
        {
            if (_freed is null)
                NoteFreeSpaceMap();
            if (_freed is null || needed < _freedWalkedFor)
            {
                _freed = _writer._file.FreedLvalPages.Of(_writer._umap.OwnedPage, _writer._umap.OwnedRow).GetEnumerator();
                _freedWalkedFor = needed;
            }
            while (_freed.MoveNext())
            {
                (int page, int room) = (_freed.Current.Key, _freed.Current.Value);
                if (room < needed || _free.ContainsKey(page) || !_freedRead.Add(page))
                    continue;
                // Freed from a value of this column, so on its map - unless the file says otherwise.
                if (UsageMap.Contains(_writer._file, _writer._umap.OwnedPage, _writer._umap.OwnedRow, page)
                    && Read(page) && HasRoom(page, needed))
                    return page;
            }
            return -1;
        }

        // The first look in a session notes the pages the column's free-space map lists - room a delete left in an
        // earlier session - to be read for their room when a chunk could fit.
        private void NoteFreeSpaceMap()
        {
            var umap = _writer._umap;
            var records = _writer._file.FreedLvalPages;
            if (umap.FreePage <= 0 || !records.FirstLookFor(umap.OwnedPage, umap.OwnedRow))
                return;
            var noted = new HashSet<int>(records.Of(umap.OwnedPage, umap.OwnedRow).Select(p => p.Key));
            byte[] mapPage = _writer._file.ReadPage(umap.FreePage);
            foreach (int page in UsageMap.GetOwnedPages(mapPage, umap.FreeRow, _writer._format, _writer._file))
                if (!noted.Contains(page))
                    records.Set(umap.OwnedPage, umap.OwnedRow, page, FreedLvalPages.NotYetRead);
        }

        private int LastPageWithRoom(int needed)
        {
            if (_lastPageRead)
                return -1;
            _lastPageRead = true;
            int last = UsageMap.GetLastPage(_writer._file, _writer._umap.OwnedPage, _writer._umap.OwnedRow);
            return last >= 0 && (_free.ContainsKey(last) || Read(last)) && HasRoom(last, needed) ? last : -1;
        }

        private int NewPage()
        {
            // A fresh LVAL data page (it carries the "LVAL" signature Access requires at bytes 4-7).
            // It is not added to the column's free-space map: the column's last page is always
            // looked at, and only the pages a delete freed are listed there.
            int page = _writer._allocator.AllocateLvalPage();
            UsageMap.AddPage(_writer._file, _writer._allocator, _writer._umap.OwnedPage, _writer._umap.OwnedRow, page);
            _free[page] = _writer._format.DataPageInitialFreeSpace;
            _rows[page] = 0;
            _open.Add(page);
            return page;
        }

        /// <summary>
        /// Reads the room and rows of <paramref name="page"/> - false, with nothing noted, unless it is a long-value
        /// page: type 0x01 with "LVAL" at bytes 4-7, as AllocateLvalPage writes one. A map that lists any other page -
        /// another table's data page - must not have chunks written onto it.
        /// </summary>
        private bool Read(int page)
        {
            byte[] dp = _writer._file.ReadPage(page);
            if (dp[0] != JetFormat.PageTypeData || dp[4] != (byte)'L' || dp[5] != (byte)'V' || dp[6] != (byte)'A' || dp[7] != (byte)'L')
                return false;
            _free[page] = ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);
            _rows[page] = ByteUtil.GetShort(dp, _writer._format.OffsetDataNumRows);
            if (_free[page] >= FreedLvalPages.MinUsefulFreeSpace)
                _open.Add(page);
            return true;
        }

        /// <summary>
        /// Once the value's chunks are written: forgets a freed page that was not this column's long-value page, and
        /// notes the room left on every page read or written that the column has noted - a freed page reached as the
        /// column's last page too - as none on a page holding as many rows as a page can.
        /// </summary>
        public void Done()
        {
            var umap = _writer._umap;
            var records = _writer._file.FreedLvalPages;
            var noted = new HashSet<int>(records.Of(umap.OwnedPage, umap.OwnedRow).Select(p => p.Key));
            foreach (int page in _freedRead)
            {
                if (!_free.ContainsKey(page))
                    Forget(page);
            }
            foreach (var entry in _free)
            {
                int room = _rows[entry.Key] < DataPageWriter.MaxRowsPerPage ? entry.Value : 0;
                if (room < FreedLvalPages.MinUsefulFreeSpace && noted.Contains(entry.Key))
                    Forget(entry.Key);
                else
                    records.Renew(umap.OwnedPage, umap.OwnedRow, entry.Key, room);
            }

            // A page with no room worth reading, or not this column's, leaves the notes and the free-space map.
            void Forget(int page)
            {
                records.Set(umap.OwnedPage, umap.OwnedRow, page, 0);
                if (umap.FreePage > 0)
                    UsageMap.RemovePage(_writer._file, umap.FreePage, umap.FreeRow, page);
            }
        }
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

    // Appends one chunk row to the GIVEN LVAL page; returns the row index.
    private int WriteChunkRowOnPage(int lvalPage, byte[] chunk)
    {
        int needed = chunk.Length + JetFormat.SizeRowEntry;

        byte[] page      = _file.ReadPage(lvalPage);
        int    rowCount  = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        if (freeSpace < needed || rowCount >= DataPageWriter.MaxRowsPerPage)
            throw new InvalidOperationException(
                $"LVAL page {lvalPage} has {freeSpace} free bytes and {rowCount} rows; a {chunk.Length}-byte chunk does not fit.");
        int    cursor    = _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int    rowStart  = cursor - chunk.Length;

        Array.Copy(chunk, 0, page, rowStart, chunk.Length);

        ByteUtil.PutShort(page,
            _format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry,
            (short)rowStart);
        ByteUtil.PutShort(page, _format.OffsetDataNumRows,   (short)(rowCount + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));

        _file.WritePage(lvalPage, page);
        return rowCount;   // rowCount was the 0-based index before increment
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
    /// OTHER_PAGE chunk. A page left with more room is noted in <see cref="PageFile.FreedLvalPages"/> for the column
    /// whose usage maps <paramref name="owner"/> names, when it has them, and added to that column's free-space map
    /// as Access lists such a page.
    /// </summary>
    public void FreeChain(int lvalPage, int lvalRow, bool chained, LvalUmapRef? owner)
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

            short freeBefore = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            TrimDeletedTail(page);

            _file.WritePage(curPage, page);
            short freeAfter = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
            if (owner is { } column && freeAfter > freeBefore)
            {
                _file.FreedLvalPages.Set(column.OwnedPage, column.OwnedRow, curPage, freeAfter);
                if (column.FreePage > 0 && freeAfter >= FreedLvalPages.MinUsefulFreeSpace)
                    UsageMap.AddPage(_file, new PageAllocator(_file), column.FreePage, column.FreeRow, curPage);
            }

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
