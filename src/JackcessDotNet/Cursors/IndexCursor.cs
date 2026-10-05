namespace JackcessDotNet;

/// <summary>
/// Cursor that supports key-based row lookup.
///
/// Lookup strategy:
///   1. The primary key, when its keys are ones this library makes: through
///      <see cref="IndexWriter"/>'s walk of the tree, whoever wrote it.
///   2. Otherwise an index on disk whose keys <see cref="IndexReader"/> makes.
///   3. Otherwise a forward table scan that compares column values: an index whose
///      keys cannot be made (a text index in another sort order) is not trusted.
/// </summary>
public sealed class IndexCursor : Cursor
{
    private readonly Table           _table;
    private readonly TableDefinition _definition;
    private readonly PageFile        _file;
    private readonly string?         _indexName;
    private readonly bool            _isPrimaryKey;

    internal IndexCursor(Table table, PageFile file, TableDefinition definition, string? indexName)
        : base(table, file, definition)
    {
        _table        = table;
        _file         = file;
        _definition   = definition;
        _indexName    = indexName;
        _isPrimaryKey = indexName is null
                     || string.Equals(indexName, "PrimaryKey", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(indexName, definition.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the first row whose primary key equals <paramref name="pkValue"/>.
    /// Returns null if no such row exists.
    /// </summary>
    public Row? FindRowByPrimaryKey(object pkValue)
    {
        if (_definition.PrimaryKeyColumnName is null)
            throw new InvalidOperationException(
                $"Table '{_definition.Name}' has no primary key column. " +
                "Use FindRowByEntry on a non-PK index, or use FindRow(column, value) for a column scan.");
        return FindRow(_definition.PrimaryKeyColumnName, pkValue);
    }

    /// <summary>
    /// Convenience: finds the first row where <paramref name="columnName"/> equals
    /// <paramref name="value"/>. Uses the primary-key index leaf when possible,
    /// otherwise falls back to a forward table scan from BeforeFirst.
    /// </summary>
    public Row? FindRow(string columnName, object value)
    {
        // Fast path #1: the primary key, through IndexWriter's own walk of the tree (prefix-compressed
        // pages and any depth), when its keys are ones IndexKeys makes.
        bool path1Eligible =
            _isPrimaryKey
            && _definition.PrimaryKeyIndexPage > 0
            && _definition.PrimaryKeyColumnName is not null
            && string.Equals(columnName, _definition.PrimaryKeyColumnName, StringComparison.OrdinalIgnoreCase)
            && IndexWriter.PrimaryKeyColumns(_definition).All(c => IndexKeys.CanEncode(c.Column));
        if (path1Eligible)
        {
            var iw = new IndexWriter(_file, new PageAllocator(_file));
            foreach (int rowPtr in iw.EnumerateRowPointersForKey(_definition, value))
            {
                // pageNum is the upper 24 bits (3-byte LE page) and rowNum the low byte.
                int pageNum = RowPointer.Page(rowPtr);
                int rowIdx  = RowPointer.Row(rowPtr);

                // Defensive: only attempt to materialise the row if pageNum points to
                // a real data page. Spurious "matches" against compressed Access leaves
                // can produce nonsense rowPtrs that would otherwise crash ReadRowAt.
                if (!IsLikelyDataPage(pageNum)) continue;

                Row? r;
                try { r = ReadRowAt(pageNum, rowIdx); }
                catch { continue; }
                if (r is null) continue;
                if (r.TryGetValue(columnName, out var stored) && ValuesEqual(stored, value))
                    return r;
            }
            // fall through
        }

        // Fast path #2: on a table read from disk, walk the real B-tree index whose
        // single column matches the requested column and whose key type we support.
        if (TryFindMatchingDiskIndex(columnName, out var diskIx))
        {
            var reader = new IndexReader(_file, diskIx!);
            foreach (int rowPtr in reader.FindRowPointers(value))
            {
                int pageNum = RowPointer.Page(rowPtr);
                int rowIdx  = RowPointer.Row(rowPtr);
                Row? r = ReadRowAt(pageNum, rowIdx);
                if (r is null) continue;
                if (r.TryGetValue(columnName, out var stored) && ValuesEqual(stored, value))
                    return r;
            }
            return null;
        }

        // Slow path: forward table scan.
        BeforeFirst();
        while (GetNextRow() is { } r)
        {
            if (r.TryGetValue(columnName, out var stored) && ValuesEqual(stored, value))
                return r;
        }
        return null;
    }

    /// <summary>
    /// Picks a disk-resident index that's usable for a single-column lookup on
    /// <paramref name="columnName"/>: must have exactly one column, that column must
    /// match by name, and its keys must be ones IndexReader makes as Access made them
    /// (a type it encodes; text sorted in General - Legacy). Returns false when no
    /// such index is present (caller falls back to scan).
    /// </summary>
    private bool TryFindMatchingDiskIndex(string columnName, out Index? matched)
    {
        foreach (var ix in _definition.Indexes)
        {
            if (ix.Columns.Count != 1) continue;
            if (ix.RootPageNumber <= 0) continue;
            if (!string.Equals(ix.Columns[0].Column.Name, columnName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IndexReader.CanResolve(ix.Columns[0].Column))
            {
                matched = ix;
                return true;
            }
        }
        matched = null;
        return false;
    }

    /// <summary>
    /// Composite-key lookup. Matches an entry whose values, in index-column declaration
    /// order, equal <paramref name="entryValues"/>. Uses the B-tree walker when an
    /// index is selected and all its column types are encodable; otherwise scans.
    /// </summary>
    public Row? FindRowByEntry(params object[] entryValues)
    {
        if (entryValues is null || entryValues.Length == 0)
            throw new ArgumentException("Entry values are required.", nameof(entryValues));

        // Index path: prefer an explicitly-named index, else any index whose column
        // count and types match the supplied entry shape.
        Index? ix = SelectIndexForEntry(entryValues.Length);
        if (ix is not null)
        {
            var reader = new IndexReader(_file, ix);
            if (reader.CanResolveKey)
            {
                foreach (int rowPtr in reader.FindRowPointersForEntry(entryValues!))
                {
                    int pageNum = RowPointer.Page(rowPtr);
                    int rowIdx  = RowPointer.Row(rowPtr);
                    Row? r = ReadRowAt(pageNum, rowIdx);
                    if (r is null) continue;
                    if (RowMatchesEntry(r, ix, entryValues))
                        return r;
                }
                return null;
            }
        }

        // Scan path: match the chosen index's columns, as its lookup would (an index whose keys
        // cannot be made: a text index in another sort order); with no index, positionally
        // against the table's columns in column-number order.
        BeforeFirst();
        while (GetNextRow() is { } r)
        {
            if (ix is not null ? RowMatchesEntry(r, ix, entryValues) : MatchesLeadingColumns(r, entryValues))
                return r;
        }
        return null;
    }

    private bool MatchesLeadingColumns(Row row, object?[] entry)
    {
        for (int i = 0; i < entry.Length && i < _definition.Columns.Count; i++)
        {
            var col = _definition.Columns[i];
            if (!row.TryGetValue(col.Name, out var stored) || !ValuesEqual(stored, entry[i]))
                return false;
        }
        return true;
    }

    private Index? SelectIndexForEntry(int entryLen)
    {
        // Prefer the explicitly-named index when it fits.
        if (_indexName is not null)
        {
            foreach (var ix in _definition.Indexes)
                if (string.Equals(ix.Name, _indexName, StringComparison.OrdinalIgnoreCase)
                    && ix.Columns.Count == entryLen)
                    return ix;
        }
        // Else any index whose column count matches the supplied entry tuple.
        foreach (var ix in _definition.Indexes)
            if (ix.Columns.Count == entryLen && ix.RootPageNumber > 0)
                return ix;
        return null;
    }

    private static bool RowMatchesEntry(Row row, Index ix, object?[] entry)
    {
        for (int i = 0; i < ix.Columns.Count; i++)
        {
            string col = ix.Columns[i].Column.Name;
            if (!row.TryGetValue(col, out var stored) || !ValuesEqual(stored, entry[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Sanity-checks that <paramref name="pageNumber"/> points at a data page (type 0x01)
    /// within file bounds. Returns false on any error so spurious rowPtrs from
    /// compressed Access leaves can't crash <see cref="ReadRowAt"/>.
    /// </summary>
    private bool IsLikelyDataPage(int pageNumber)
    {
        if (pageNumber <= 0) return false;
        try
        {
            byte[] page = _file.ReadPage(pageNumber);
            return page[0] == JetFormat.PageTypeData;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValuesEqual(object? stored, object? requested)
    {
        if (Equals(stored, requested)) return true;
        if (stored is null || requested is null) return false;
        try   { return Convert.ToDecimal(stored) == Convert.ToDecimal(requested); }
        catch { return false; }
    }
}
