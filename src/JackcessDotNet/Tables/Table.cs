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

        // Decide about the indexes *before* writing the row: if an index cannot be maintained
        // and the caller has not opted out of the check, nothing should be written at all.
        var skipIndexes = PlanIndexMaintenance(row);

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
    /// When <c>false</c> (the default) an insert that cannot keep one of the table's indexes
    /// correct throws, and neither the row nor the index is written. Set it to <c>true</c> to
    /// insert anyway, leaving that index without an entry for the new row.
    /// <para>
    /// It has almost nothing left to affect: leaf splits, pages Access prefix-compressed, and full
    /// nodes are all written correctly now, so the refusal it overrides no longer fires for any
    /// index this library can reach — only for a key too large to share a page with any other,
    /// which Jet's 255-byte key limit puts out of reach. It is kept because callers set it and
    /// because a future unsupported case should have somewhere to opt out.
    /// </para>
    /// <para>
    /// When it does suppress an entry, nothing is corrupted, but Access reads through indexes — it
    /// will answer <c>COUNT(*)</c> from the primary key rather than scanning — so it can
    /// under-report rows that are physically present.
    /// </para>
    /// </summary>
    public bool ForceIgnoreIndexCheck { get; set; }

    /// <summary>
    /// Works out which indexes cannot take an entry for this row, and enforces
    /// <see cref="ForceIgnoreIndexCheck"/>. Returns the ordinals to leave alone.
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

            if (!ForceIgnoreIndexCheck)
                throw new NotSupportedException(
                    $"Index '{ix.Name}' on '{Name}' is full at two levels, and this row would " +
                    "need its root node split to grow a third — that is not written yet. This row " +
                    "was not written and no index was modified, so the file stays valid; rows " +
                    "inserted earlier in the same batch remain. Set Table.ForceIgnoreIndexCheck (or " +
                    $"ImportOptions.ForceIgnoreIndexCheck) to true to keep inserting and leave " +
                    $"'{ix.Name}' without entries from this point on; Access may then under-report " +
                    "rows for queries that use it. Leaf splits and pages Access prefix-compressed " +
                    "need none of this — both are written correctly.");

            skip.Add(i);
        }

        return skip;
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
        int newRowPtr = _dataWriter.UpdateRowByPrimaryKey(_definition, primaryKeyValue, newValues);

        // The merged row that was actually written carries the (unchanged) PK value, so we can
        // index it under primaryKeyValue without inspecting newValues for the PK column.
        if (_definition.PrimaryKeyColumnName is not null && _definition.PrimaryKeyIndexPage > 0)
            new IndexWriter(_file, _allocator)
                .InsertPrimaryKey(_definition, primaryKeyValue, newRowPtr);
    }

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
        _dataWriter.DeleteRow(_definition, columnName, value);
        _dataWriter.IncrementTdefRowCount(_definition.TdefPageNumber, -1);
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
