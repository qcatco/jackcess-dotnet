using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Represents an open user table and exposes row-insertion operations.
/// Instances are created by <see cref="Database.CreateTable"/> or
/// <see cref="Database.GetTable"/>.
/// </summary>
public sealed class Table
{
    private readonly PageFile       _file;
    private readonly PageAllocator  _allocator;
    private readonly TableDefinition _definition;
    private readonly DataPageWriter  _dataWriter;
    private readonly Database?       _owningDb;

    internal Table(PageFile file, PageAllocator allocator, TableDefinition definition,
                   Database? owningDb = null)
    {
        _file        = file        ?? throw new ArgumentNullException(nameof(file));
        _allocator   = allocator   ?? throw new ArgumentNullException(nameof(allocator));
        _definition  = definition  ?? throw new ArgumentNullException(nameof(definition));
        _dataWriter  = new DataPageWriter(file, allocator);
        _owningDb    = owningDb;
    }

    // ── Public properties ─────────────────────────────────────────────────────

    public string                Name    => _definition.Name;
    public IReadOnlyList<Column> Columns => _definition.Columns;
    /// <summary>Indexes defined on this table (read from the on-disk TDEF).</summary>
    public IReadOnlyList<Index>  Indexes => _definition.Indexes;

    internal TableDefinition     Definition => _definition;

    private PropertyMaps? _propertiesCache;
    /// <summary>
    /// Property maps attached to this table in MSysObjects.LvProp. The
    /// <c>Default</c> map holds table-level properties (Description, Caption, etc.);
    /// per-column properties live in named maps keyed by column name.
    /// Lazily decoded on first access; null bytes → empty <see cref="PropertyMaps"/>.
    /// </summary>
    public PropertyMaps Properties
    {
        get
        {
            if (_propertiesCache is not null) return _propertiesCache;
            var catalog = new SystemCatalog(_file);
            byte[]? bytes = catalog.GetPropertyBytesForObject(_definition.TdefPageNumber);
            _propertiesCache = PropertyMapReader.Read(bytes, _file.Format);
            return _propertiesCache;
        }
    }

    // ── Cursor factories ──────────────────────────────────────────────────────

    /// <summary>Creates a sequential table-scan cursor (Jackcess-shape API).</summary>
    public Cursor      NewCursor()                               => NewCursorInternal();

    /// <summary>Creates an index cursor that supports FindRow / FindRowByPrimaryKey.</summary>
    public IndexCursor NewIndexCursor(string? indexName = null)  => NewIndexCursorInternal(indexName);

    internal Cursor      NewCursorInternal()                       => new Cursor(this, _file, _definition);
    internal IndexCursor NewIndexCursorInternal(string? indexName) => new IndexCursor(this, _file, _definition, indexName);

    // ── Row operations ────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes and inserts <paramref name="row"/> into the table.
    /// A new data page is allocated automatically if the current one is full.
    /// The total row count in the table's TDEF is updated on every insert.
    /// When the table has a primary-key column configured, a leaf entry is also
    /// appended to the PK index page so <see cref="IndexCursor.FindRow"/> can use it.
    /// </summary>
    public void Insert(Row row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));

        _owningDb?.ValidateForeignKeysForInsert(this, row);

        // Decide about the indexes *before* writing the row: if one cannot be kept correct, or the
        // row would repeat a key a unique index promises is unique, nothing should be written.
        var skipIndexes = PlanIndexMaintenance(row);
        EnforceUniqueIndexes(row);

        int rowPtr = _dataWriter.InsertRow(_definition, row);
        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber);
        AddIndexEntries(row, rowPtr, skipIndexes);
    }

    /// <summary>
    /// Returns the values behind a complex column (multi-value field, attachment field, or
    /// append-only memo) for one row — read-only.
    /// <para>
    /// The row stores only a 4-byte id; the values live in a per-column flat table. Each
    /// returned <see cref="Row"/> is a row of that table, so the shape depends on the kind of
    /// complex column: a multi-value field exposes <c>Value</c>, an attachment field exposes
    /// <c>FileName</c>, <c>FileType</c>, <c>FileData</c>, <c>FileTimeStamp</c>, <c>FileFlags</c>
    /// and <c>FileURL</c>, and a version-history field exposes the memo value with its
    /// timestamp.
    /// </para>
    /// </summary>
    /// <returns>
    /// The matching flat-table rows, or an empty list when the column is not complex, the row
    /// carries no id, or the database has no complex-column catalog.
    /// </returns>
    public IReadOnlyList<Row> GetComplexValues(Row row, string columnName)
    {
        if (row is null)        throw new ArgumentNullException(nameof(row));
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));

        var column = _definition.Columns.FirstOrDefault(
            c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null || column.DataType != DataType.Complex) return Array.Empty<Row>();

        if (!row.TryGetValue(column.Name, out object? idValue) || idValue is not int complexId || complexId == 0)
            return Array.Empty<Row>();

        if (_owningDb is null) return Array.Empty<Row>();

        var flat = ComplexColumns.ResolveFlatTable(_owningDb, _definition.TdefPageNumber, column.Name);
        if (flat is null) return Array.Empty<Row>();

        var backRef = ComplexColumns.FindBackReference(flat, Name, column.Name);
        if (backRef is null) return Array.Empty<Row>();

        var matches = new List<Row>();
        foreach (Row candidate in flat.ReadAllRows())
            if (candidate.TryGetValue(backRef.Name, out object? owner) && owner is int ownerId && ownerId == complexId)
                matches.Add(candidate);

        return matches;
    }

    /// <summary>
    /// Appends a value to a complex column of <paramref name="row"/> — one entry of a multi-value
    /// field, one attachment, one memo-history revision.
    /// <para>
    /// The values live in a per-column flat table, linked to the owning row by its 4-byte complex
    /// id. <paramref name="value"/> supplies that table's own columns — <c>Value</c> for a
    /// multi-value field, <c>FileName</c>/<c>FileType</c>/<c>FileData</c>… for an attachment — and
    /// the two link columns are filled here: the foreign key back to the owning row, and the flat
    /// row's own sequential id, which Access numbers per flat table rather than per owning row.
    /// </para>
    /// <para>
    /// An attachment's <c>FileData</c> is written exactly as given. Access stores attachment bytes
    /// behind a small header of its own and may compress them, so a payload written raw here reads
    /// back byte-for-byte through this library but is not what Access would have written.
    /// </para>
    /// </summary>
    /// <returns>The row as written to the flat table, link columns included.</returns>
    /// <exception cref="InvalidOperationException">
    /// The column is not complex, the row carries no complex id, or the flat table behind the
    /// column cannot be resolved.
    /// </exception>
    public Row AddComplexValue(Row row, string columnName, Row value)
    {
        if (row is null)        throw new ArgumentNullException(nameof(row));
        if (columnName is null) throw new ArgumentNullException(nameof(columnName));
        if (value is null)      throw new ArgumentNullException(nameof(value));

        var column = _definition.Columns.FirstOrDefault(
            c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null || column.DataType != DataType.Complex)
            throw new InvalidOperationException(
                $"Column '{columnName}' on '{Name}' is not a complex column.");

        if (!row.TryGetValue(column.Name, out object? idValue) || idValue is not int complexId || complexId == 0)
            throw new InvalidOperationException(
                $"The row carries no complex id for '{columnName}', so there is nothing to attach a " +
                "value to. Complex ids are assigned when the owning row is created.");

        if (_owningDb is null)
            throw new InvalidOperationException("This table is not attached to a database.");

        var flat = ComplexColumns.ResolveFlatTable(_owningDb, _definition.TdefPageNumber, column.Name)
            ?? throw new InvalidOperationException(
                $"No flat table is registered for complex column '{columnName}' on '{Name}'.");

        var backRef = ComplexColumns.FindBackReference(flat, Name, column.Name)
            ?? throw new InvalidOperationException(
                $"The flat table for '{columnName}' has no column linking back to '{Name}'.");

        var written = new Row();
        foreach (var kvp in value) written[kvp.Key] = kvp.Value;
        written[backRef.Name] = complexId;

        // The flat row's own id counts up across the whole flat table, not per owning row, so it
        // continues from the highest already stored.
        var ownId = ComplexColumns.FindOwnId(flat, Name, column.Name, backRef);
        if (ownId is not null && !written.ContainsKey(ownId.Name))
            written[ownId.Name] = NextComplexOwnId(flat, ownId);

        flat.Insert(written);
        return written;
    }

    private static int NextComplexOwnId(Table flat, Column ownId)
    {
        int highest = 0;
        foreach (var existing in flat.ReadAllRows())
            if (existing.TryGetValue(ownId.Name, out object? v) && v is int id && id > highest)
                highest = id;
        return highest + 1;
    }

    /// <summary>
    /// Works out which indexes have no entry to take for this row — one with no tree, or one that
    /// ignores nulls when a key column is null — and returns their ordinals so
    /// <see cref="AddIndexEntries"/> leaves them alone. An index that could not be kept correct at
    /// all throws instead, before anything is written.
    /// </summary>
    private HashSet<int> PlanIndexMaintenance(Row row)
    {
        var skip = new HashSet<int>();
        if (_definition.Indexes.Count == 0) return skip;

        var writer = new IndexWriter(_file, _allocator);

        for (int i = 0; i < _definition.Indexes.Count; i++)
        {
            var ix = _definition.Indexes[i];
            if (ix.RootPageNumber <= 0 || ix.Columns.Count == 0) { skip.Add(i); continue; }

            var values = IndexValuesFor(ix, row, out bool anyNull);
            if (anyNull && ix.IgnoresNulls) { skip.Add(i); continue; }

            if (!writer.WouldExceedIndexCapacity(_definition, ix, values)) continue;

            throw new NotSupportedException(
                $"The key this row takes in index '{ix.Name}' on '{Name}' is too large for an " +
                "index page to hold three of them, which is what splitting a node needs, so the " +
                "index cannot grow to take it. This row was not written and no index was modified, " +
                "so the file stays valid; rows inserted earlier in the same batch remain. Index " +
                "fewer or shorter columns.");
        }

        return skip;
    }

    /// <summary>
    /// Rejects a row that would repeat a key held by a unique index — every primary key, and any
    /// secondary index created with <c>unique: true</c>.
    /// <para>
    /// A unique index tells Access no key repeats, and Access reads the index trusting that. Letting
    /// a duplicate through produced a file Access considers corrupt rather than one it merely
    /// disagrees with. Checked before anything is written, so a rejected row leaves nothing behind.
    /// </para>
    /// <para>
    /// A key with a null component is exempt: SQL treats null as unequal to everything, itself
    /// included. Access counts nulls as values here and would reject the second one, so this is a
    /// deliberate divergence.
    /// </para>
    /// </summary>
    private void EnforceUniqueIndexes(Row row)
    {
        var seen = new HashSet<int>();

        foreach (var ix in IndexesForMaintenance())
        {
            if (!ix.IsUnique || ix.RootPageNumber <= 0 || ix.Columns.Count == 0) continue;
            if (!seen.Add(ix.IndexDataNumber)) continue;

            var values = IndexValuesFor(ix, row, out bool anyNull);
            if (anyNull) continue;

            if (FindExistingKey(ix, values) is int clashingRowPtr)
                throw new InvalidOperationException(
                    $"Index '{ix.Name}' on '{Name}' is unique and already holds the key " +
                    $"({string.Join(", ", values)}), on the row at page " +
                    $"{(clashingRowPtr >> 16) & 0xFFFFFF} slot {clashingRowPtr & 0xFF}. " +
                    "Nothing was written.");
        }
    }

    /// <summary>
    /// The row pointer of a live row already carrying this key, or null.
    /// <para>
    /// The index is only a starting point: an entry can outlive its row in a file written before
    /// deletes kept indexes in step, so each candidate is read back and its values compared. A
    /// stale entry must not block an insert.
    /// </para>
    /// </summary>
    private int? FindExistingKey(Index ix, IReadOnlyList<object?> values)
    {
        var reader = new IndexReader(_file, ix);
        var cursor = NewCursorInternal();

        var pointers = ix.Columns.Count == 1
            ? reader.FindRowPointers(values[0]!)
            : reader.FindRowPointersForEntry(values.ToArray());

        foreach (int rowPtr in pointers)
        {
            Row? existing;
            try { existing = cursor.ReadRowAt((rowPtr >> 16) & 0xFFFFFF, rowPtr & 0xFF); }
            catch { continue; }
            if (existing is null) continue;

            bool allMatch = true;
            for (int i = 0; i < ix.Columns.Count && allMatch; i++)
            {
                existing.TryGetValue(ix.Columns[i].Column.Name, out object? stored);
                allMatch = ValuesMatch(stored, values[i]);
            }
            if (allMatch) return rowPtr;
        }

        return null;
    }

    private static List<object?> IndexValuesFor(Index ix, Row row, out bool anyNull)
    {
        anyNull = false;
        var values = new List<object?>(ix.Columns.Count);
        foreach (var ic in ix.Columns)
        {
            row.TryGetValue(ic.Column.Name, out object? v);
            if (v is null) anyNull = true;
            values.Add(v);
        }
        return values;
    }

    /// <summary>
    /// Threads a just-inserted row into <b>every</b> index the table declares, not only the
    /// primary key.
    /// <para>
    /// Access reads through indexes — it will answer <c>COUNT(*)</c> from the primary-key tree
    /// rather than scanning — so a row missing from an index simply does not exist as far as
    /// Access is concerned, while this library's own page scan still returns it. Appending 500
    /// rows to an Access-authored table with a primary key used to leave Access reporting one
    /// row: its own.
    /// </para>
    /// </summary>
    private void AddIndexEntries(Row row, int rowPtr, HashSet<int> skipIndexes)
    {
        // A table created in this session exposes only PrimaryKeyIndexPage — its TDEF index
        // blocks have not been read back into Indexes — so keep the PK-only path for it.
        if (_definition.Indexes.Count == 0)
        {
            MaybeAddPrimaryKeyIndexEntry(row, rowPtr);
            return;
        }

        var writer = new IndexWriter(_file, _allocator);

        // Several logical indexes can share one index-data block — Access points a table's
        // foreign-key index and its primary key at the same tree when they cover the same
        // columns. That tree takes one entry, so maintain each block once, not once per slot.
        var maintained = new HashSet<int>();

        for (int i = 0; i < _definition.Indexes.Count; i++)
        {
            if (skipIndexes.Contains(i)) continue;

            var ix = _definition.Indexes[i];
            if (!maintained.Add(ix.IndexDataNumber)) continue;

            var values = IndexValuesFor(ix, row, out _);

            writer.InsertIntoIndex(_definition, ix, values, rowPtr);
            writer.IncrementIndexRowCount(_definition, ix);
        }
    }

    /// <summary>
    /// Updates the row with the given primary-key value.
    /// Old LVAL chunks are freed and their space is reclaimed before the new row is written.
    /// A new PK index entry is appended pointing at the rewritten row; the old entry is left
    /// behind as a stale pointer that <see cref="IndexCursor"/> filters out via slot/PK checks.
    /// </summary>
    public void UpdateByPrimaryKey(object primaryKeyValue, Row newValues)
    {
        string? pkColumn = _definition.PrimaryKeyColumnName;
        if (pkColumn is null)
        {
            _dataWriter.UpdateRowByPrimaryKey(_definition, primaryKeyValue, newValues);
            return;
        }

        int? rowPtr = ResolveRowPointer(pkColumn, primaryKeyValue);
        if (rowPtr is null)
            throw new InvalidOperationException(
                $"No row with primary key '{primaryKeyValue}' found in table '{Name}'.");

        // One lookup gives both versions of the row: an update rewrites it at a new pointer and may
        // change an indexed value, so every index needs the old entry out and the new one in.
        var updated = _dataWriter.UpdateRowAt(_definition, rowPtr.Value, newValues);
        if (updated is null)
            throw new InvalidOperationException(
                $"The row for primary key '{primaryKeyValue}' in table '{Name}' could not be read " +
                $"back at the pointer it was found at (p{(rowPtr.Value >> 16) & 0xFFFFFF}, " +
                $"slot {rowPtr.Value & 0xFF}); nothing was updated.");

        var (before, after, newRowPtr) = updated.Value;
        RemoveIndexEntries(before, rowPtr.Value);
        AddIndexEntries(after, newRowPtr, new HashSet<int>());
    }

    /// <summary>
    /// The row pointer of the first row whose <paramref name="columnName"/> equals
    /// <paramref name="value"/>.
    /// <para>
    /// Seeks through a single-column index on that column when there is one, which is three or four
    /// page reads instead of reading every data page in the table. A composite index is no use
    /// here — its entries are keyed on all of its columns at once — and without a usable index this
    /// falls back to the scan.
    /// </para>
    /// </summary>
    private int? ResolveRowPointer(string columnName, object value)
    {
        var ix = IndexesForMaintenance().FirstOrDefault(
            i => i.RootPageNumber > 0
              && i.Columns.Count == 1
              && i.Columns[0].Column.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        if (ix is not null)
        {
            var cursor = NewCursorInternal();
            foreach (int rowPtr in new IndexReader(_file, ix).FindRowPointers(value))
            {
                // An index can hold an entry whose row no longer matches — an older file written
                // before delete kept indexes in step, for one — so the row is checked, not trusted.
                Row? row;
                try { row = cursor.ReadRowAt((rowPtr >> 16) & 0xFFFFFF, rowPtr & 0xFF); }
                catch { continue; }

                if (row is not null && row.TryGetValue(columnName, out object? stored)
                    && ValuesMatch(stored, value))
                    return rowPtr;
            }
            // An index that yielded nothing usable is not proof of absence, so fall through.
        }

        foreach (var (row, rowPtr) in EnumerateRowsWithPointers())
            if (row.TryGetValue(columnName, out object? stored) && ValuesMatch(stored, value))
                return rowPtr;

        return null;
    }

    /// <summary>
    /// Compares a stored value with a caller's, tolerating numeric width — a key read back as
    /// <c>long</c> or <c>decimal</c> still matches the <c>int</c> it was written from.
    /// </summary>
    private static bool ValuesMatch(object? stored, object? requested)
    {
        if (Equals(stored, requested)) return true;
        if (stored is null || requested is null) return false;
        try { return Convert.ToDecimal(stored) == Convert.ToDecimal(requested); }
        catch { return false; }
    }

    /// <summary>
    /// Takes a row out of every index that holds it, and decrements each one's entry count.
    /// <para>
    /// Access answers <c>COUNT(*)</c> from an index and follows its entries without checking that
    /// the row still matches, so an entry left behind for a removed row both over-reports and
    /// points at a slot that no longer holds what it claims. This library's own
    /// <see cref="IndexCursor"/> filters those, which is why the omission stayed invisible to it.
    /// </para>
    /// </summary>
    private void RemoveIndexEntries(Row row, int rowPtr)
    {
        var writer     = new IndexWriter(_file, _allocator);
        var maintained = new HashSet<int>();

        foreach (var ix in IndexesForMaintenance())
        {
            if (ix.RootPageNumber <= 0 || ix.Columns.Count == 0) continue;
            if (!maintained.Add(ix.IndexDataNumber)) continue;

            var values = IndexValuesFor(ix, row, out bool anyNull);
            if (anyNull && ix.IgnoresNulls) continue;

            if (writer.RemoveFromIndex(_definition, ix, values, rowPtr))
                writer.IncrementIndexRowCount(_definition, ix, -1);
        }
    }

    /// <summary>
    /// The indexes to keep in step. A table read back from disk lists them; one created in this
    /// session has no index metadata yet, only the primary-key tree it just wrote, so that is
    /// described here rather than left unmaintained.
    /// </summary>
    private IEnumerable<Index> IndexesForMaintenance()
    {
        if (_definition.Indexes.Count > 0) return _definition.Indexes;

        var pkCols = _definition.EffectivePrimaryKeyColumns;
        if (pkCols.Count == 0 || _definition.PrimaryKeyIndexPage <= 0) return Array.Empty<Index>();

        var columns = new List<IndexColumn>(pkCols.Count);
        foreach (string name in pkCols)
        {
            var col = _definition.Columns.FirstOrDefault(
                c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (col is null) return Array.Empty<Index>();
            columns.Add(new IndexColumn(col, AscendingIndexColumnFlag));
        }

        return new[]
        {
            new Index("PrimaryKey", columns, _definition.PrimaryKeyIndexPage,
                      indexNumber: 0, flags: PrimaryKeyIndexFlags, indexType: PrimaryKeyIndexType,
                      indexDataNumber: PrimaryKeyDataBlock),
        };
    }

    private const byte AscendingIndexColumnFlag = 0x01;
    private const byte PrimaryKeyIndexFlags     = 0x89;   // unknown | unique | required
    private const byte PrimaryKeyIndexType      = 0x01;

    private void MaybeAddPrimaryKeyIndexEntry(Row row, int rowPtr)
    {
        if (_definition.PrimaryKeyIndexPage == 0) return;

        var pkCols = _definition.EffectivePrimaryKeyColumns;
        if (pkCols.Count == 0) return;

        var writer = new IndexWriter(_file, _allocator);
        if (pkCols.Count == 1)
        {
            // Single-column path: missing-or-null skips the index entry, matching
            // the existing (pre-composite) behaviour exactly.
            if (!row.TryGetValue(pkCols[0], out var pkValue) || pkValue is null) return;
            writer.InsertPrimaryKey(_definition, pkValue, rowPtr);
            writer.IncrementIndexRowCountForDataBlock(_definition, PrimaryKeyDataBlock);
        }
        else
        {
            // Composite path: every component must be present + non-null.
            var values = new object?[pkCols.Count];
            for (int i = 0; i < pkCols.Count; i++)
            {
                if (!row.TryGetValue(pkCols[i], out var v) || v is null) return;
                values[i] = v;
            }
            writer.InsertPrimaryKey(_definition, values, rowPtr);
            writer.IncrementIndexRowCountForDataBlock(_definition, PrimaryKeyDataBlock);
        }
    }

    /// <summary>
    /// The index-data block holding the primary key of a table created in this session. Such a
    /// table has exactly one index and writes its blocks in slot order, so the block is 0. Tables
    /// read back from disk never reach here — they go through <see cref="AddIndexEntries"/>, which
    /// takes each block from its own <see cref="Index.IndexDataNumber"/>.
    /// </summary>
    private const int PrimaryKeyDataBlock = 0;

    /// <summary>
    /// Deletes the first row where <paramref name="columnName"/> equals <paramref name="value"/>.
    /// Any LVAL page chains held by the deleted row are freed and their space is reclaimed.
    /// The TDEF row count is decremented by one.
    /// </summary>
    public void DeleteRow(string columnName, object value)
    {
        int? rowPtr = ResolveRowPointer(columnName, value);
        if (rowPtr is null)
            throw new InvalidOperationException(
                $"No row with {columnName} = '{value}' found in table '{Name}'.");

        // Deleting by pointer hands back the row as it was, which is what the index entries were
        // built from — so one lookup covers both the delete and the index upkeep.
        Row? deleted = _dataWriter.DeleteRowAt(_definition, rowPtr.Value);
        if (deleted is null)
            throw new InvalidOperationException(
                $"The row for {columnName} = '{value}' in table '{Name}' could not be read back at " +
                $"the pointer it was found at (p{(rowPtr.Value >> 16) & 0xFFFFFF}, " +
                $"slot {rowPtr.Value & 0xFF}); nothing was deleted.");

        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber, -1);
        RemoveIndexEntries(deleted, rowPtr.Value);
    }

    /// <summary>
    /// Reads and decodes every non-deleted row in the table and returns them in
    /// insertion order.  Follows LVAL page chains for Memo/OLE columns.
    /// </summary>
    public IReadOnlyList<Row> ReadAllRows()
        => EnumerateRowsWithPointers().Select(r => r.Row).ToList();

    /// <summary>
    /// The same scan as <see cref="ReadAllRows"/>, keeping each row's pointer — the
    /// <c>(page &lt;&lt; 16) | rowIndex</c> identity an index entry has to point at, packed the
    /// way <c>DataPageWriter.InsertRow</c> returns it.
    /// </summary>
    internal List<(Row Row, int RowPointer)> EnumerateRowsWithPointers()
    {
        var format     = _file.Format;
        var lvalReader = _definition.LvalColumnUmapPages.Count > 0
            ? new LvalReader(_file)
            : null;
        var decoder = new RowDecoder(format, _definition.Columns, lvalReader);

        byte[] umapPage  = _file.ReadPage(_definition.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, _definition.OwnedPagesRow, format, _file);

        var result = new List<(Row, int)>();
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
                    : (ByteUtil.GetUShort(dp,
                           format.OffsetDataRowTable + (r - 1) * JetFormat.SizeRowEntry)
                       & JetFormat.RowOffsetMask);
                int rowLen = rowEnd - rowStart;
                if (rowLen <= 0) continue;

                var rowBytes = new byte[rowLen];
                Array.Copy(dp, rowStart, rowBytes, 0, rowLen);

                var row = new Row();
                foreach (var col in _definition.Columns)
                {
                    object? val = decoder.Decode(rowBytes, col);
                    if (val is not null)
                        row[col.Name] = val;
                }
                result.Add((row, (pageNum << 16) | r));
            }
        }
        return result;
    }

    /// <summary>
    /// Fills a just-created index from the rows already in the table. An index Access can see but
    /// that answers nothing is worse than no index at all, since Access reads through it.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The tree outgrew what the writer can maintain partway through, leaving the index
    /// incomplete — see <see cref="IndexWriter.WouldExceedIndexCapacity"/>.
    /// </exception>
    internal void BackfillIndex(string indexName)
    {
        var ix = _definition.Indexes.FirstOrDefault(
            i => i.Name.Equals(indexName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Table '{Name}' has no index named '{indexName}' to fill.");

        var writer = new IndexWriter(_file, _allocator);
        int done   = 0;

        foreach (var (row, rowPtr) in EnumerateRowsWithPointers())
        {
            var values = IndexValuesFor(ix, row, out bool anyNull);
            if (anyNull && ix.IgnoresNulls) continue;

            if (writer.WouldExceedIndexCapacity(_definition, ix, values))
                throw new NotSupportedException(
                    $"Index '{ix.Name}' on '{Name}' outgrew what this writer can maintain after " +
                    $"{done} of the table's rows, so it is now incomplete and Access would " +
                    "under-report rows for queries that use it. The index is on disk: drop it in " +
                    "Access, or rebuild the table with fewer rows per index page.");

            writer.InsertIntoIndex(_definition, ix, values, rowPtr);
            writer.IncrementIndexRowCount(_definition, ix);
            done++;
        }
    }
}
