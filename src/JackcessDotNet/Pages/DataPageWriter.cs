using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes rows into Jet data pages (page type 0x01).
///
/// Data-page slot-array layout:
///   Bytes  0..13  – header (page type, free-space, tdef-page#, row-count)
///   Bytes 14..    – row slot table, 2 bytes per slot, grows forward
///   … free space …
///   …             – row data, packed from end of page, grows backward
///
/// Slot N value = absolute byte-offset of row N's data inside the page.
/// Row 0 is the first row inserted and is placed at the highest address.
/// Row N (N>0) is placed immediately before row N-1.
///
/// Free-space formula:
///   freeSpace = row1Start − ( OffsetDataRowTable + rowCount × SizeRowEntry )
///             = minSlotValue − headerUsed
/// Equivalently:
///   cursor (next write position) = OffsetDataRowTable + rowCount×2 + freeSpace
///   newRowStart = cursor − rowSize
/// </summary>
public sealed class DataPageWriter
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    public DataPageWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes <paramref name="row"/> and appends it to the best available data page
    /// for <paramref name="tableDef"/>.  Allocates a new data page if needed and
    /// registers it in the table's owned-pages usage-map.
    /// </summary>
    /// <returns>
    /// A packed row-pointer: <c>pageNumber &lt;&lt; 16 | rowIndexOnPage</c>.
    /// </returns>
    public int InsertRow(TableDefinition tableDef, Row row)
    {
        if (tableDef is null) throw new ArgumentNullException(nameof(tableDef));
        if (row      is null) throw new ArgumentNullException(nameof(row));

        var format = _file.Format;

        // Build per-column LvalWriters for any Memo/OLE columns that have a umap page.
        Dictionary<string, LvalWriter>? lvalWriters = null;
        foreach (var col in tableDef.Columns)
        {
            if (col.DataType.IsLongValue() &&
                tableDef.LvalColumnUmapPages.TryGetValue(col.Name, out int umapPage))
            {
                lvalWriters ??= new Dictionary<string, LvalWriter>(StringComparer.OrdinalIgnoreCase);
                tableDef.LvalColumnUmapRows.TryGetValue(col.Name, out int umapRow);
                lvalWriters[col.Name] = new LvalWriter(_file, _allocator, umapPage, umapRow);
            }
        }

        var    encoder = new RowEncoder(format, tableDef.Columns, lvalWriters);
        byte[] rowData = encoder.Encode(row);

        int dataPage   = FindOrAllocateDataPage(tableDef, rowData.Length, format);
        int rowNum     = WriteRowOnPage(dataPage, rowData, tableDef.TdefPageNumber, format);

        return (dataPage << 16) | rowNum;
    }

    /// <summary>
    /// Updates the total-row-count field inside the table's TDEF page by adding
    /// <paramref name="delta"/> to the existing value.
    /// </summary>
    public void IncrementTdefRowCount(int tdefPageNumber, int delta = 1)
    {
        var format    = _file.Format;
        var tdefPage  = _file.ReadPage(tdefPageNumber);
        int current   = ByteUtil.GetInt(tdefPage, format.TdefOffsetNumRows);
        ByteUtil.PutInt(tdefPage, format.TdefOffsetNumRows, current + delta);
        _file.WritePage(tdefPageNumber, tdefPage);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private int FindOrAllocateDataPage(TableDefinition tableDef, int rowDataSize, JetFormat format)
    {
        int needed = rowDataSize + JetFormat.SizeRowEntry;   // data bytes + one slot

        byte[] umapPage = _file.ReadPage(tableDef.UmapPageNumber);

        // A table keeps a second usage map listing just the pages that still have room, which is
        // what makes finding one cheap. Walking the owned-pages map instead — every page the table
        // has ever used — read the whole table on every insert, so loading n rows cost O(n²) page
        // reads. The map is a hint, not a promise: each candidate's own free-space field decides.
        bool umapChanged = false;
        foreach (int pageNum in UsageMap.GetOwnedPages(umapPage, tableDef.FreeSpaceRow, format, _file))
        {
            byte[] dp = _file.ReadPage(pageNum);

            if (dp[0] == JetFormat.PageTypeData)
            {
                int freeSpace = ByteUtil.GetShort(dp, JetFormat.OffsetDataFreeSpace);

                // Short on room, but deleted rows may be holding some. Closing their gaps costs a
                // page rewrite, so it is only worth doing when the page is about to be given up on.
                if (freeSpace < needed)
                {
                    int reclaimed = CompactPage(pageNum);
                    if (reclaimed >= 0) freeSpace = reclaimed;
                }

                if (freeSpace >= needed)
                {
                    if (umapChanged) _file.WritePage(tableDef.UmapPageNumber, umapPage);
                    return pageNum;
                }
            }

            // A candidate that cannot take this row is dropped, as Jackcess Java does. Keeping it
            // listed until it is nearly full instead leaves pages with an awkward amount of room in
            // the map for good, and every later insert pays to read them again — which is the O(n²)
            // this map exists to avoid. Some space goes unused when row sizes vary; a delete that
            // empties a page puts it back.
            umapChanged |= UsageMap.RemovePage(umapPage, tableDef.FreeSpaceRow, pageNum, format, _file);
        }
        if (umapChanged) _file.WritePage(tableDef.UmapPageNumber, umapPage);

        // Ask the usage-map before allocating: allocating extends the file straight away,
        // so a map that cannot record the page must not cost one. (Recording it first
        // isn't an option — a map that promotes itself allocates bitmap pages, which
        // would take the very number the data page is about to get.)
        int newPage = _allocator.NextPageNumber;
        if (!UsageMap.CanAddPage(umapPage, tableDef.OwnedPagesRow, newPage, format, out string? refusal))
            throw new NotSupportedException(refusal);

        int allocated = _allocator.AllocateDataPage(tableDef.TdefPageNumber);
        if (allocated != newPage)
            throw new InvalidOperationException(
                $"Allocated data page {allocated} but the usage-map was checked for page {newPage}.");

        UsageMap.AddPage(umapPage, tableDef.OwnedPagesRow, allocated, format, _allocator, _file);

        // An empty page has room by definition, so it joins the free-space map too — otherwise the
        // map stays empty and every insert allocates a page of its own.
        if (UsageMap.CanAddPage(umapPage, tableDef.FreeSpaceRow, allocated, format, out _))
            UsageMap.AddPage(umapPage, tableDef.FreeSpaceRow, allocated, format, _allocator, _file);

        _file.WritePage(tableDef.UmapPageNumber, umapPage);

        return allocated;
    }

    /// <summary>
    /// Closes the gaps a page's deleted rows have left, returning how much free space it ends up
    /// with — or -1 when there was nothing to reclaim.
    /// <para>
    /// Deleting a row only flags its slot. The bytes stay, and because the page's free space is the
    /// gap between the slot table and the lowest row, they are unusable until the surviving rows
    /// are packed back together.
    /// </para>
    /// <para>
    /// <b>Slots keep their numbers.</b> An index entry points at <c>(page &lt;&lt; 16) | slot</c>,
    /// so renumbering would leave every index in the table pointing at the wrong rows — the reason
    /// Access reclaims this only during a compact-and-repair, where it rebuilds the indexes too.
    /// Only the offsets inside the slots change, and each row is re-laid in slot order so that
    /// row <c>i</c> still sits directly below row <c>i-1</c>, which is the ordering the readers
    /// use to find where a row ends.
    /// </para>
    /// </summary>
    private int CompactPage(int pageNum)
    {
        var    format = _file.Format;
        byte[] page   = _file.ReadPage(pageNum);
        if (page[0] != JetFormat.PageTypeData) return -1;

        int rowCount = ByteUtil.GetShort(page, format.OffsetDataNumRows);
        if (rowCount <= 0) return -1;

        // Gather the live rows with the slots they must stay in.
        var live = new List<(int Slot, ushort SlotVal, byte[] Bytes)>(rowCount);
        bool anyDeleted = false;

        for (int r = 0; r < rowCount; r++)
        {
            int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
            ushort slotVal = ByteUtil.GetUShort(page, slotOff);

            if ((slotVal & 0x8000) != 0) { anyDeleted = true; continue; }
            if ((slotVal & 0x4000) != 0) return -1;   // an overflow pointer: not ours to move

            int start = slotVal & JetFormat.RowOffsetMask;
            int end   = r == 0
                ? format.PageSize
                : ByteUtil.GetUShort(page, format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                  & JetFormat.RowOffsetMask;
            if (end <= start) return -1;   // not a layout this can safely rewrite

            var bytes = new byte[end - start];
            Array.Copy(page, start, bytes, 0, bytes.Length);
            live.Add((r, slotVal, bytes));
        }

        if (!anyDeleted) return -1;

        // Re-lay the survivors from the end of the page down, in slot order. A deleted slot keeps
        // its flag and takes the cursor as its offset, so it spans zero bytes and the
        // "row i ends where row i-1 starts" rule still holds across it.
        int cursor = format.PageSize;
        var offsets = new int[rowCount];

        int next = 0;
        for (int r = 0; r < rowCount; r++)
        {
            if (next < live.Count && live[next].Slot == r)
            {
                byte[] bytes = live[next].Bytes;
                cursor -= bytes.Length;
                Array.Copy(bytes, 0, page, cursor, bytes.Length);
                offsets[r] = cursor;
                next++;
            }
            else
            {
                offsets[r] = cursor;   // deleted: zero-length at the current boundary
            }
        }

        for (int r = 0; r < rowCount; r++)
        {
            int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
            ushort flags   = (ushort)(ByteUtil.GetUShort(page, slotOff) & ~JetFormat.RowOffsetMask);
            ByteUtil.PutUShort(page, slotOff, (ushort)(flags | (ushort)offsets[r]));
        }

        int freeSpace = cursor - (format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry);
        if (freeSpace < 0) return -1;

        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)freeSpace);
        _file.WritePage(pageNum, page);
        return freeSpace;
    }

    /// <summary>
    /// Puts a page back on the free-space map, for a page that has just regained room. Absent-check
    /// first: adding a page already listed is harmless to the bitmap but writes the map again.
    /// </summary>
    private void ListAsFree(TableDefinition table, int dataPage)
    {
        var    format   = _file.Format;
        byte[] umapPage = _file.ReadPage(table.UmapPageNumber);

        if (UsageMap.GetOwnedPages(umapPage, table.FreeSpaceRow, format, _file).Contains(dataPage))
            return;
        if (!UsageMap.CanAddPage(umapPage, table.FreeSpaceRow, dataPage, format, out _))
            return;

        UsageMap.AddPage(umapPage, table.FreeSpaceRow, dataPage, format, _allocator, _file);
        _file.WritePage(table.UmapPageNumber, umapPage);
    }

    private int WriteRowOnPage(int pageNumber, byte[] rowData, int tdefPageNumber, JetFormat format)
    {
        byte[] page      = _file.ReadPage(pageNumber);
        int    rowCount  = ByteUtil.GetShort(page, format.OffsetDataNumRows);
        int    freeSpace = ByteUtil.GetShort(page, JetFormat.OffsetDataFreeSpace);
        int    needed    = rowData.Length + JetFormat.SizeRowEntry;

        if (freeSpace < needed)
            throw new InvalidOperationException(
                $"Data page {pageNumber} has only {freeSpace} free bytes; row needs {needed}.");

        // cursor = first byte of the free gap (grows up), which also equals the
        // left-edge of the already-written data area when rearranged:
        //   cursor = OffsetDataRowTable + rowCount×2 + freeSpace
        int cursor      = format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry + freeSpace;
        int rowStart    = cursor - rowData.Length;

        // Copy row data into the page
        Array.Copy(rowData, 0, page, rowStart, rowData.Length);

        // Write the slot entry for this row
        int slotOffset  = format.OffsetDataRowTable + rowCount * JetFormat.SizeRowEntry;
        ByteUtil.PutShort(page, slotOffset, (short)rowStart);

        // Update header
        ByteUtil.PutShort(page, format.OffsetDataNumRows,   (short)(rowCount  + 1));
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)(freeSpace - needed));

        _file.WritePage(pageNumber, page);
        return rowCount;   // 0-based row index on this page
    }

    // ── UpdateRowByPrimaryKey ─────────────────────────────────────────────────

    /// <summary>
    /// Finds the row whose primary-key column equals <paramref name="primaryKeyValue"/>
    /// (linear scan of all owned data pages), merges <paramref name="newValues"/> into it,
    /// marks the old row as deleted, and re-inserts the merged row.
    /// Returns the packed rowPtr of the new row: <c>pageNumber &lt;&lt; 16 | rowIndex</c>.
    /// </summary>
    public int UpdateRowByPrimaryKey(TableDefinition table, object primaryKeyValue, Row newValues)
    {
        if (table    is null) throw new ArgumentNullException(nameof(table));
        if (newValues is null) throw new ArgumentNullException(nameof(newValues));

        if (table.PrimaryKeyColumnName is null)
            throw new InvalidOperationException(
                "PrimaryKeyColumnName is not set on this TableDefinition. " +
                "Specify a primary key column when calling Database.CreateTable.");

        var format  = _file.Format;
        var columns = table.Columns;
        var lvalReader = table.LvalColumnUmapPages.Count > 0
            ? new LvalReader(_file)
            : null;
        var decoder = new RowDecoder(format, columns, lvalReader);

        // Locate the primary-key column descriptor.
        Column? pkCol = null;
        foreach (var col in columns)
        {
            if (col.Name.Equals(table.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase))
            {
                pkCol = col;
                break;
            }
        }
        if (pkCol is null)
            throw new InvalidOperationException(
                $"Primary key column '{table.PrimaryKeyColumnName}' not found in table '{table.Name}'.");

        byte[] umapPage  = _file.ReadPage(table.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, table.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
                ushort slotVal = ByteUtil.GetUShort(dp, slotOff);

                if ((slotVal & 0x8000) != 0) continue;   // deleted
                if ((slotVal & 0x4000) != 0) continue;   // overflow pointer

                int rowStart = slotVal & JetFormat.RowOffsetMask;
                int rowEnd   = (r == 0)
                    ? format.PageSize
                    : (ByteUtil.GetUShort(dp, format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                object? pkVal = decoder.Decode(rowBytes, pkCol);
                if (pkVal is null || !PrimaryKeysEqual(pkVal, primaryKeyValue)) continue;

                // Build the merged row: existing values overlaid by newValues.
                var merged = new Row();
                foreach (var col in columns)
                {
                    object? existing = decoder.Decode(rowBytes, col);
                    if (existing is not null)
                        merged[col.Name] = existing;
                }
                foreach (var kvp in newValues)
                    merged[kvp.Key] = kvp.Value;

                // Free any LVAL chains referenced by the old row before overwriting.
                FreeRowLvalChains(rowBytes, decoder);

                // Mark the original slot as deleted (bit 15).
                ByteUtil.PutUShort(dp, slotOff, (ushort)(slotVal | 0x8000u));
                _file.WritePage(pageNum, dp);

                // Insert the merged row and propagate its rowPtr so the caller can
                // update any PK index that points at the old (now-deleted) row.
                return InsertRow(table, merged);
            }
        }

        throw new InvalidOperationException(
            $"No row with primary key '{primaryKeyValue}' found in table '{table.Name}'.");
    }

    // ── DeleteRow ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the first row where <paramref name="columnName"/> equals <paramref name="value"/>
    /// (linear scan), frees its LVAL chains, and marks the slot as deleted.
    /// The caller is responsible for decrementing the TDEF row count.
    /// </summary>
    public void DeleteRow(TableDefinition table, string columnName, object value)
    {
        if (table is null)      throw new ArgumentNullException(nameof(table));
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));

        Column? targetCol = null;
        foreach (var col in table.Columns)
        {
            if (col.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                targetCol = col;
                break;
            }
        }
        if (targetCol is null)
            throw new InvalidOperationException(
                $"Column '{columnName}' not found in table '{table.Name}'.");

        var format     = _file.Format;
        var lvalReader = table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null;
        var decoder    = new RowDecoder(format, table.Columns, lvalReader);

        byte[] umapPage  = _file.ReadPage(table.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, table.OwnedPagesRow, format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                int    slotOff = format.OffsetDataRowTable + r * JetFormat.SizeRowEntry;
                ushort slotVal = ByteUtil.GetUShort(dp, slotOff);

                if ((slotVal & 0x8000) != 0) continue;   // already deleted
                if ((slotVal & 0x4000) != 0) continue;   // overflow pointer

                int rowStart = slotVal & JetFormat.RowOffsetMask;
                int rowEnd   = (r == 0)
                    ? format.PageSize
                    : (ByteUtil.GetUShort(dp,
                           format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                object? colVal = decoder.Decode(rowBytes, targetCol);
                if (!PrimaryKeysEqual(colVal, value)) continue;

                // Free any LVAL chains referenced by this row.
                FreeRowLvalChains(rowBytes, decoder);

                // Mark the slot as deleted.
                ByteUtil.PutUShort(dp, slotOff, (ushort)(slotVal | 0x8000u));
                _file.WritePage(pageNum, dp);
                return;
            }
        }

        throw new InvalidOperationException(
            $"No row with {columnName} = '{value}' found in table '{table.Name}'.");
    }

    // ── Pointer-addressed variants ────────────────────────────────────────────

    /// <summary>
    /// Deletes the row at <paramref name="rowPointer"/> — <c>(page &lt;&lt; 16) | rowIndex</c>, as
    /// <see cref="InsertRow"/> returns and an index entry stores — freeing its LVAL chains and
    /// marking the slot deleted. Returns the row as it was, which the caller needs to take its
    /// index entries out. The TDEF row count is the caller's job.
    /// <para>
    /// This exists so a caller holding a pointer does not pay for the linear scan
    /// <see cref="DeleteRow"/> does: an index seek already knows exactly which slot to free.
    /// </para>
    /// </summary>
    /// <returns>The deleted row, or <c>null</c> when the slot holds nothing usable.</returns>
    public Row? DeleteRowAt(TableDefinition table, int rowPointer)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));

        var decoder = DecoderFor(table);
        var located  = LocateRow(table, rowPointer, decoder);
        if (located is null) return null;

        var (page, pageNum, slotOffset, slotVal, rowBytes) = located.Value;

        var row = new Row();
        foreach (var col in table.Columns)
        {
            object? v = decoder.Decode(rowBytes, col);
            if (v is not null) row[col.Name] = v;
        }

        FreeRowLvalChains(rowBytes, decoder);
        ByteUtil.PutUShort(page, slotOffset, (ushort)(slotVal | 0x8000u));
        _file.WritePage(pageNum, page);

        ReclaimIfEmptied(table, pageNum);

        // The page now holds space worth having, even though its free-space figure has not moved —
        // the bytes are still occupied by the flagged row until something compacts them. Listing it
        // is what gives the next insert the chance to: page selection compacts a candidate before
        // giving up on it. Without this the page is simply never looked at again, having been
        // evicted from the map when it filled.
        ListAsFree(table, pageNum);
        return row;
    }

    /// <summary>
    /// Resets a data page and puts it back on the free-space map once every row on it is deleted.
    /// <para>
    /// Deleting a row only flags its slot; the bytes stay where they are and the page's free-space
    /// field does not move, so that space is not reusable. Reclaiming it properly means compacting
    /// the page — sliding the surviving rows together and rewriting the slot table — which is not
    /// done. But a page with nothing left on it needs no compaction: it can go back to empty as a
    /// whole, which covers deleting a batch of rows and is what lets a file that churns stop growing
    /// without bound.
    /// </para>
    /// </summary>
    private void ReclaimIfEmptied(TableDefinition table, int pageNum)
    {
        var    format = _file.Format;
        byte[] page   = _file.ReadPage(pageNum);
        int    rows   = ByteUtil.GetShort(page, format.OffsetDataNumRows);
        if (rows == 0) return;

        for (int r = 0; r < rows; r++)
        {
            ushort slot = ByteUtil.GetUShort(
                page, format.OffsetDataRowTable + r * JetFormat.SizeRowEntry);
            if ((slot & 0x8000) == 0) return;   // a live row remains
        }

        // Rewrite as an empty data page belonging to the same table.
        Array.Clear(page, 0, page.Length);
        page[0] = JetFormat.PageTypeData;
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)format.DataPageInitialFreeSpace);
        ByteUtil.PutInt(page, JetFormat.OffsetDataTdefPage, table.TdefPageNumber);
        _file.WritePage(pageNum, page);

        ListAsFree(table, pageNum);
    }

    /// <summary>
    /// Overlays <paramref name="newValues"/> onto the row at <paramref name="rowPointer"/> and
    /// rewrites it, returning the row as it was, the merged row, and the pointer the merged row
    /// now lives at. The old slot is marked deleted, as Jet does for an update that moves a row.
    /// </summary>
    public (Row Before, Row After, int NewRowPointer)? UpdateRowAt(
        TableDefinition table, int rowPointer, Row newValues)
    {
        if (table     is null) throw new ArgumentNullException(nameof(table));
        if (newValues is null) throw new ArgumentNullException(nameof(newValues));

        var decoder = DecoderFor(table);
        var located = LocateRow(table, rowPointer, decoder);
        if (located is null) return null;

        var (page, pageNum, slotOffset, slotVal, rowBytes) = located.Value;

        var before = new Row();
        foreach (var col in table.Columns)
        {
            object? v = decoder.Decode(rowBytes, col);
            if (v is not null) before[col.Name] = v;
        }

        var after = new Row();
        foreach (var kvp in before)    after[kvp.Key] = kvp.Value;
        foreach (var kvp in newValues) after[kvp.Key] = kvp.Value;

        FreeRowLvalChains(rowBytes, decoder);
        ByteUtil.PutUShort(page, slotOffset, (ushort)(slotVal | 0x8000u));
        _file.WritePage(pageNum, page);

        return (before, after, InsertRow(table, after));
    }

    private RowDecoder DecoderFor(TableDefinition table)
        => new RowDecoder(_file.Format, table.Columns,
                          table.LvalColumnUmapPages.Count > 0 ? new LvalReader(_file) : null);

    /// <summary>
    /// Resolves a row pointer to its page, slot and raw bytes. Returns null when the slot is
    /// deleted, an overflow pointer, or out of range — a stale index entry can point at any of
    /// those, and following one blindly would corrupt an unrelated row.
    /// </summary>
    private (byte[] Page, int PageNum, int SlotOffset, ushort SlotVal, byte[] RowBytes)? LocateRow(
        TableDefinition table, int rowPointer, RowDecoder decoder)
    {
        var format  = _file.Format;
        int pageNum = (rowPointer >> 16) & 0xFFFFFF;
        int rowIdx  = rowPointer & 0xFF;
        if (pageNum <= 0) return null;

        byte[] page = _file.ReadPage(pageNum);
        if (page[0] != JetFormat.PageTypeData) return null;

        int rowCount = ByteUtil.GetShort(page, format.OffsetDataNumRows);
        if (rowIdx < 0 || rowIdx >= rowCount) return null;

        int    slotOffset = format.OffsetDataRowTable + rowIdx * JetFormat.SizeRowEntry;
        ushort slotVal    = ByteUtil.GetUShort(page, slotOffset);
        if ((slotVal & 0x8000) != 0) return null;   // already deleted
        if ((slotVal & 0x4000) != 0) return null;   // overflow pointer

        int rowStart = slotVal & JetFormat.RowOffsetMask;
        int rowEnd   = rowIdx == 0
            ? format.PageSize
            : ByteUtil.GetUShort(page, format.OffsetDataRowTable + (rowIdx - 1) * JetFormat.SizeRowEntry)
              & JetFormat.RowOffsetMask;
        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return null;

        var rowBytes = new byte[rowLen];
        Array.Copy(page, rowStart, rowBytes, 0, rowLen);
        return (page, pageNum, slotOffset, slotVal, rowBytes);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    // Frees every OTHER_PAGE LVAL chain referenced by the given row bytes.
    private void FreeRowLvalChains(byte[] rowBytes, RowDecoder decoder)
    {
        var lvalFree = new LvalFree(_file);
        foreach (var (lvalPage, lvalRow) in decoder.GetOtherPageLvRefs(rowBytes))
            lvalFree.FreeChain(lvalPage, lvalRow);
    }

    private static bool PrimaryKeysEqual(object? stored, object? requested)
    {
        if (Equals(stored, requested)) return true;
        if (stored is null || requested is null) return false;
        try { return Convert.ToDecimal(stored) == Convert.ToDecimal(requested); }
        catch { return false; }
    }
}
