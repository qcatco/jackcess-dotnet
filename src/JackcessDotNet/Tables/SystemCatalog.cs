using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Reads from and writes to the MSysObjects system catalog (TDEF always at page 2).
///
/// A table is registered the way Access registers one it creates, or Access
/// cannot find it:
///   • a row in MSysObjects: Id = its TDEF page, Name, Type 1, DateCreate and
///     DateUpdate = now, ParentId = the "Tables" container's Id, Flags 0, and
///     Owner = the Admin user (all other columns NULL);
///   • that row's entries in MSysObjects' indexes (Id, and ParentIdName, through
///     which Access finds a table by name);
///   • rows in MSysACEs giving the new table the permissions the Tables container
///     passes on, with their entries in MSysACEs' index.
/// </summary>
public sealed class SystemCatalog
{
    private readonly PageFile       _file;
    private readonly JetFormat      _format;
    private readonly PageAllocator  _allocator;
    private readonly DataPageWriter _writer;

    // Well-known MSysObjects column names
    private const string ColId         = "Id";
    private const string ColName       = "Name";
    private const string ColType       = "Type";
    private const string ColDateCreate = "DateCreate";
    private const string ColDateUpdate = "DateUpdate";
    private const string ColParentId   = "ParentId";
    private const string ColFlags      = "Flags";
    private const string ColLvProp     = "LvProp";
    private const string ColOwner      = "Owner";

    // MSysACEs' columns
    private const string AceAcm         = "ACM";
    private const string AceInheritable = "FInheritable";
    private const string AceObjectId    = "ObjectId";
    private const string AceSid         = "SID";

    /// <summary>The ParentId of the top-level containers ("Tables", "Databases", ...).</summary>
    private const int DatabaseParentId = 0x0F000000;
    private const short CatalogTypeContainer = 3;

    /// <summary>
    /// The permissions a SID holds on the Tables container, passed on to tables
    /// created in it, that Access gives it on a new table: in the Access-made
    /// tables of the test corpus, a table has one ACE for each inheritable
    /// container ACE holding all of these bits, with the same ACM.
    /// </summary>
    private const int TableFullAccess = 0x000FFEFF;

    public SystemCatalog(PageFile file)
    {
        _file      = file   ?? throw new ArgumentNullException(nameof(file));
        _format    = file.Format;
        _allocator = new PageAllocator(file);
        _writer    = new DataPageWriter(file, _allocator);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void CreateBaseCatalog()
    {
        // No-op: the embedded empty-database template already contains MSysObjects.
    }

    /// <summary>
    /// Refuses, before anything is written, a table this library cannot register as
    /// Access would: a name an object in the Tables container already has, in any
    /// case (a table, a query or a linked table: they share one set of names), or a
    /// catalog with an index whose keys it cannot write (names sorted in an order
    /// other than General - Legacy, as Access 2010 and later make them).
    /// </summary>
    public void EnsureCanRegister(string tableName) => Prepare(tableName);

    /// <summary>
    /// Registers a new user table: its MSysObjects row and index entries, and its
    /// permissions in MSysACEs (see the class summary). Refuses what
    /// <see cref="EnsureCanRegister"/> refuses, before writing anything.
    /// </summary>
    public void InsertTableEntry(string tableName, int tdefPageNumber)
    {
        var (catalogDef, tablesId, owner, acesDef) = Prepare(tableName);

        var now = DateTime.Now;
        var row = new Row
        {
            [ColId]         = tdefPageNumber,
            [ColName]       = tableName,
            [ColType]       = (short)JetFormat.CatalogTypeTable,
            [ColDateCreate] = now,
            [ColDateUpdate] = now,
            [ColParentId]   = tablesId,
            [ColFlags]      = 0
        };
        if (owner is not null) row[ColOwner] = owner;

        int rowPtr = _writer.InsertRow(catalogDef, row);
        _writer.IncrementTdefRowCount(JetFormat.PageSystemCatalog);
        AddIndexEntries(catalogDef, row, rowPtr);

        if (acesDef is not null)
            InsertAccessControlEntries(acesDef, tablesId, tdefPageNumber, owner);
    }

    /// <summary>
    /// Gives the new object <paramref name="objectId"/> an ACE, not inheritable, for
    /// each inheritable ACE of the Tables container holding <see cref="TableFullAccess"/>,
    /// with that ACE's SID and ACM - what Access writes for a table it creates. A
    /// container without one gives the owner those permissions.
    /// </summary>
    private void InsertAccessControlEntries(TableDefinition acesDef, int tablesId, int objectId, byte[]? owner)
    {
        var granted = ReadRows(acesDef, AceAcm, AceInheritable, AceObjectId, AceSid)
            .Where(ace => Value(ace, AceObjectId) is int id && id == tablesId
                       && Value(ace, AceInheritable) is true
                       && Value(ace, AceAcm) is int acm && (acm & TableFullAccess) == TableFullAccess
                       && Value(ace, AceSid) is byte[])
            .Select(ace => ((int)Value(ace, AceAcm)!, (byte[])Value(ace, AceSid)!))
            .ToList();
        if (granted.Count == 0 && owner is not null)
            granted.Add((TableFullAccess, owner));

        foreach (var (acm, sid) in granted)
        {
            var ace = new Row
            {
                [AceAcm]         = acm,
                [AceInheritable] = false,
                [AceObjectId]    = objectId,
                [AceSid]         = sid,
            };
            int rowPtr = _writer.InsertRow(acesDef, ace);
            _writer.IncrementTdefRowCount(acesDef.TdefPageNumber);
            AddIndexEntries(acesDef, ace, rowPtr);
        }
    }

    /// <summary>
    /// The Admin user's SID, which Access makes a new table's owner. A file keeps its
    /// SIDs encoded with a key of its own; the Admin user's is the system objects'
    /// owner with 0x0102 mixed in, in every Access-made table of the test corpus
    /// (Access 97 to 2019). Null when MSysObjects' own owner is not a 2-byte SID.
    /// </summary>
    private static byte[]? AdminOwner(IEnumerable<Row> catalog)
    {
        var system = catalog.FirstOrDefault(r =>
            Value(r, ColName) is string name && name == "MSysObjects"
            && Value(r, ColType) is short type && type == JetFormat.CatalogTypeTable);
        return system is not null && Value(system, ColOwner) is byte[] { Length: 2 } sid
            ? new[] { (byte)(sid[0] ^ 0x01), (byte)(sid[1] ^ 0x02) }
            : null;
    }

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    // What registering a table needs, read and checked before anything is written.
    private (TableDefinition Catalog, int TablesId, byte[]? Owner, TableDefinition? Aces) Prepare(string tableName)
    {
        var catalogDef = BuildCatalogTableDef();
        var catalog    = ReadRows(catalogDef, ColId, ColName, ColType, ColParentId, ColOwner);

        var tables = catalog.FirstOrDefault(r =>
                Value(r, ColType) is short type && type == CatalogTypeContainer
                && Value(r, ColParentId) is int parent && parent == DatabaseParentId
                && Value(r, ColName) is string name && name == "Tables")
            ?? throw new InvalidDataException("MSysObjects has no Tables container to put a table in.");
        int tablesId = (int)Value(tables, ColId)!;

        if (catalog.Any(r => Value(r, ColParentId) is int parent && parent == tablesId
                          && Value(r, ColName) is string name
                          && string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"An object named '{tableName}' already exists in this database.");

        int acesTdef = FindTableTdefPage("MSysACEs");
        var acesDef  = acesTdef >= 0 ? BuildTableDef("MSysACEs", acesTdef) : null;

        // Every index must be one this library can write a key for.
        EnsureKeysCanBeWritten(catalogDef);
        if (acesDef is not null) EnsureKeysCanBeWritten(acesDef);

        return (catalogDef, tablesId, AdminOwner(catalog), acesDef);
    }

    private static void EnsureKeysCanBeWritten(TableDefinition def)
    {
        foreach (var index in def.Indexes)
            foreach (var column in index.Columns)
                if (!IndexKeys.CanEncode(column.Column))
                    throw new NotSupportedException(
                        $"{def.Name}'s index {index.Name} cannot be kept: {IndexKeys.WhyNot(column.Column)}.");
    }

    /// <summary>
    /// Adds the row's entry to each of the table's indexes: once per index block, as
    /// two logical indexes can share one.
    /// </summary>
    private void AddIndexEntries(TableDefinition def, Row row, int rowPtr)
    {
        var writer = new IndexWriter(_file, _allocator);
        foreach (var index in def.Indexes.GroupBy(ix => ix.IndexDataNumber).Select(g => g.First()))
        {
            var values = index.Columns.Select(c => Value(row, c.Column.Name)).ToArray();
            writer.Insert(IndexWriter.Target.For(def.TdefPageNumber, index), IndexKeys.Encode(index.Columns, values), rowPtr);
        }
    }

    /// <summary>The <paramref name="columns"/> of every row of the table <paramref name="def"/> describes.</summary>
    private List<Row> ReadRows(TableDefinition def, params string[] columns)
    {
        var wanted  = def.Columns.Where(c => columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var decoder = new RowDecoder(_format, def.Columns);
        var rows    = new List<Row>();

        byte[] umapPage = _file.ReadPage(def.UmapPageNumber);
        foreach (int pageNum in UsageMap.GetOwnedPages(umapPage, def.OwnedPagesRow, _format, _file))
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);
            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;
                var row = new Row();
                foreach (var column in wanted)
                    row[column.Name] = decoder.Decode(rowBytes, column);
                rows.Add(row);
            }
        }
        return rows;
    }

    // Flags stored on each MSysObjects row; matches Jackcess constants.
    private const int SystemObjectFlag    = unchecked((int)0x80000000);
    private const int AltSystemObjectFlag = 0x02;

    /// <summary>
    /// Loads the property bytes ("LvProp" column) for the MSysObjects row whose
    /// Id equals <paramref name="objectTdefPage"/>. Returns null for any row that
    /// has a null/empty LvProp value (most tables have none). The result is the
    /// raw blob; <see cref="PropertyMapReader"/> turns it into a <see cref="PropertyMaps"/>.
    /// </summary>
    public byte[]? GetPropertyBytesForObject(int objectTdefPage)
    {
        var catalogDef = BuildCatalogTableDef();
        var columns    = catalogDef.Columns;
        Column? colId    = columns.FirstOrDefault(c => c.Name.Equals(ColId,    StringComparison.OrdinalIgnoreCase));
        Column? colLvProp= columns.FirstOrDefault(c => c.Name.Equals(ColLvProp,StringComparison.OrdinalIgnoreCase));
        if (colId is null || colLvProp is null) return null;

        var decoder    = new RowDecoder(_format, columns);   // no LvalReader → we read raw LvRef ourselves
        var lvalReader = new LvalReader(_file);

        byte[] umapPage  = _file.ReadPage(catalogDef.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, catalogDef.OwnedPagesRow, _format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;

                object? idVal = decoder.Decode(rowBytes, colId);
                if (idVal is not int id || id != objectTdefPage) continue;

                // Find the LvProp column's variable-length payload manually so we
                // can pull the raw bytes (property bytes are arbitrary, not text).
                return ReadVariableColumnRawBytes(rowBytes, colLvProp, columns, lvalReader);
            }
        }
        return null;
    }

    /// <summary>
    /// Locates <paramref name="targetCol"/>'s slice within <paramref name="rowBytes"/>'
    /// variable-length area and, if it's a long-value (Memo/OLE) column with an
    /// OTHER_PAGE LvRef, follows the chain to assemble the full byte array.
    /// </summary>
    private byte[]? ReadVariableColumnRawBytes(
        byte[] rowBytes, Column targetCol,
        IReadOnlyList<Column> allColumns, LvalReader lvalReader)
    {
        if (!targetCol.DataType.IsVariableLength()) return null;
        if (rowBytes.Length < _format.SizeRowColumnCount) return null;

        int colCount = _format.SizeRowColumnCount == 2
            ? ByteUtil.GetShort(rowBytes, 0)
            : rowBytes[0];

        int maskSize   = (colCount + 7) / 8;
        int maskOffset = rowBytes.Length - maskSize;
        if (maskOffset < _format.SizeRowColumnCount) return null;

        bool notNull = (rowBytes[maskOffset + targetCol.ColumnNumber / 8]
                        & (1 << (targetCol.ColumnNumber % 8))) != 0;
        if (!notNull) return null;

        // Determine this column's var-index. Either trust the on-disk TDEF value
        // or compute by enumerating variable-length columns in column-number order.
        int varIdx = targetCol.VarLenTableIndex >= 0
            ? targetCol.VarLenTableIndex
            : CountPrecedingVarColumns(targetCol, allColumns);

        int sz = _format.SizeRowVarColOffset;
        int varCountOffset = maskOffset - sz;
        if (varCountOffset < 0) return null;
        int storedVarCount = sz == 2
            ? ByteUtil.GetShort(rowBytes, varCountOffset)
            : rowBytes[varCountOffset];
        if (varIdx >= storedVarCount) return null;

        int varStartOffset = maskOffset - (varIdx + 2) * sz;
        if (varStartOffset < 0) return null;
        int varStart = sz == 2 ? ByteUtil.GetShort(rowBytes, varStartOffset) : rowBytes[varStartOffset];
        int varEnd;
        if (varIdx + 1 < storedVarCount)
        {
            int nextOffset = maskOffset - (varIdx + 1 + 2) * sz;
            varEnd = sz == 2 ? ByteUtil.GetShort(rowBytes, nextOffset) : rowBytes[nextOffset];
        }
        else
        {
            int eodOffset = maskOffset - (storedVarCount + 2) * sz;
            if (eodOffset < 0) return null;
            varEnd = sz == 2 ? ByteUtil.GetShort(rowBytes, eodOffset) : rowBytes[eodOffset];
        }

        int varLen = varEnd - varStart;
        if (varLen < 4 || varStart < 0 || varEnd > maskOffset) return null;

        // Access LvRef header (12 bytes total in the row's var area, per Jackcess):
        //   bytes 0..3 = lengthWithFlags (LE int): high byte = type, low 24 bits = length
        //   THIS_PAGE   (type 0x80): bytes 4..11 reserved, then inline data at byte 12
        //   OTHER_PAGE  (type 0x40): byte 4 = rowNum, bytes 5..7 = page (LE 3-byte)
        //   OTHER_PAGES (type 0x00): same first 4 bytes, but chunks chain across pages
        if (varLen < 4) return null;
        int lenWithFlags = ByteUtil.GetInt(rowBytes, varStart);
        byte typeFlag   = (byte)((uint)lenWithFlags >> 24);
        int  dataLen    = lenWithFlags & 0x00FFFFFF;
        if (typeFlag == 0x80)
        {
            // Inline data starts 12 bytes into the LvRef.
            int avail = varLen - 12;
            if (avail <= 0) return Array.Empty<byte>();
            int actualLen = Math.Min(dataLen, avail);
            var inline = new byte[actualLen];
            Array.Copy(rowBytes, varStart + 12, inline, 0, actualLen);
            return inline;
        }
        if ((typeFlag == 0x40 || typeFlag == 0x00) && varLen >= 8)
        {
            int lvalRow  = rowBytes[varStart + 4];
            int lvalPage = rowBytes[varStart + 5]
                         | (rowBytes[varStart + 6] << 8)
                         | (rowBytes[varStart + 7] << 16);
            return typeFlag == 0x40
                ? ReadAccessLvalSingleChunk(lvalPage, lvalRow, dataLen)
                : ReadAccessLvalChain      (lvalPage, lvalRow, dataLen);
        }
        return null;
    }

    /// <summary>
    /// Single-chunk OTHER_PAGE read: the entire value lives in one row on an LVAL page.
    /// Row layout: chunk data starts at the row's first byte and runs to rowEnd.
    /// </summary>
    private byte[] ReadAccessLvalSingleChunk(int pageNum, int rowNum, int dataLen)
    {
        byte[] page = _file.ReadPage(pageNum);
        int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
        if (rowNum >= rowCount) return Array.Empty<byte>();

        int slotOff  = _format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry;
        int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
        int rowEnd   = (rowNum == 0)
            ? _format.PageSize
            : (ByteUtil.GetUShort(page,
                   _format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);
        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return Array.Empty<byte>();

        int copyLen = Math.Min(dataLen, rowLen);
        var result = new byte[copyLen];
        Array.Copy(page, rowStart, result, 0, copyLen);
        return result;
    }

    /// <summary>
    /// Chunk-chain OTHER_PAGES read: each chunk row begins with a 4-byte
    /// (1-byte nextRow + 3-byte LE nextPage) header, followed by data bytes to rowEnd.
    /// </summary>
    private byte[] ReadAccessLvalChain(int pageNum, int rowNum, int dataLen)
    {
        var result = new byte[dataLen];
        int written = 0;

        while (written < dataLen)
        {
            byte[] page = _file.ReadPage(pageNum);
            int rowCount = ByteUtil.GetShort(page, _format.OffsetDataNumRows);
            if (rowNum >= rowCount) break;

            int slotOff  = _format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry;
            int rowStart = ByteUtil.GetUShort(page, slotOff) & JetFormat.RowOffsetMask;
            int rowEnd   = (rowNum == 0)
                ? _format.PageSize
                : (ByteUtil.GetUShort(page,
                       _format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
                   & JetFormat.RowOffsetMask);
            int rowLen = rowEnd - rowStart;
            if (rowLen < 4) break;

            // Header at the start of the chunk row: nextRow + nextPage(3LE)
            int nextRow  = page[rowStart];
            int nextPage = page[rowStart + 1]
                         | (page[rowStart + 2] << 8)
                         | (page[rowStart + 3] << 16);

            int chunkLen = rowLen - 4;
            int copyLen  = Math.Min(chunkLen, dataLen - written);
            Array.Copy(page, rowStart + 4, result, written, copyLen);
            written += copyLen;

            if (nextPage == 0 || (nextPage == 0 && nextRow == 0)) break;
            pageNum = nextPage;
            rowNum  = nextRow;
        }
        return result;
    }

    private static int CountPrecedingVarColumns(Column target, IReadOnlyList<Column> all)
    {
        int idx = 0;
        foreach (var c in all)
        {
            if (!c.DataType.IsVariableLength()) continue;
            if (c.ColumnNumber == target.ColumnNumber) return idx;
            idx++;
        }
        return idx;
    }

    /// <summary>
    /// Returns the names of all user tables (or, with <paramref name="includeSystem"/>,
    /// all tables including MSys*). Linked tables are not yet distinguished.
    /// </summary>
    public IReadOnlyList<string> GetTableNames(bool includeSystem)
    {
        var result = new List<string>();
        ForEachTableEntry((name, _, flags) =>
        {
            bool isSystem = (flags & (SystemObjectFlag | AltSystemObjectFlag)) != 0;
            if (isSystem && !includeSystem) return;
            result.Add(name);
        });
        return result;
    }

    /// <summary>
    /// Scans MSysObjects data pages and returns the TDEF page number for
    /// <paramref name="tableName"/>, or -1 if not found.
    /// </summary>
    public int FindTableTdefPage(string tableName)
    {
        int found = -1;
        ForEachTableEntry((name, tdefPage, _) =>
        {
            if (found < 0 && string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase))
                found = tdefPage;
        });
        return found;
    }

    /// <summary>
    /// Iterates every Type=1 row in MSysObjects and invokes <paramref name="visit"/>
    /// with (name, tdefPageNumber, flags). Used by both FindTableTdefPage and GetTableNames.
    /// </summary>
    private void ForEachTableEntry(Action<string, int, int> visit)
    {
        var catalogDef = BuildCatalogTableDef();
        var columns    = catalogDef.Columns;
        var decoder    = new RowDecoder(_format, columns);

        Column? colId    = columns.FirstOrDefault(c => c.Name.Equals(ColId,    StringComparison.OrdinalIgnoreCase));
        Column? colName  = columns.FirstOrDefault(c => c.Name.Equals(ColName,  StringComparison.OrdinalIgnoreCase));
        Column? colType  = columns.FirstOrDefault(c => c.Name.Equals(ColType,  StringComparison.OrdinalIgnoreCase));
        Column? colFlags = columns.FirstOrDefault(c => c.Name.Equals(ColFlags, StringComparison.OrdinalIgnoreCase));

        if (colId is null || colName is null || colType is null)
            throw new InvalidOperationException(
                "Could not locate required columns (Id, Name, Type) in MSysObjects TDEF.");

        byte[] umapPage  = _file.ReadPage(catalogDef.UmapPageNumber);
        var    ownedList = UsageMap.GetOwnedPages(umapPage, catalogDef.OwnedPagesRow, _format, _file);

        foreach (int pageNum in ownedList)
        {
            byte[] dp       = _file.ReadPage(pageNum);
            int    rowCount = ByteUtil.GetShort(dp, _format.OffsetDataNumRows);

            for (int r = 0; r < rowCount; r++)
            {
                byte[]? rowBytes = ReadRowBytes(dp, r, _format);
                if (rowBytes is null) continue;

                object? typeVal = decoder.Decode(rowBytes, colType);
                if (typeVal is not short typeShort || typeShort != JetFormat.CatalogTypeTable)
                    continue;

                object? nameVal = decoder.Decode(rowBytes, colName);
                if (nameVal is not string name) continue;

                object? idVal = decoder.Decode(rowBytes, colId);
                if (idVal is not int id) continue;

                int flags = 0;
                if (colFlags is not null && decoder.Decode(rowBytes, colFlags) is int f)
                    flags = f;

                visit(name, id, flags);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private TableDefinition BuildCatalogTableDef() => BuildTableDef("MSysObjects", JetFormat.PageSystemCatalog);

    private TableDefinition BuildTableDef(string name, int tdefPageNumber)
    {
        byte[] tdefPage = _file.ReadPage(tdefPageNumber);
        var    info     = TdefReader.Read(tdefPage, _format);

        return new TableDefinition(name, info.Columns)
        {
            TdefPageNumber = tdefPageNumber,
            UmapPageNumber = info.OwnedPagesUmapPage,
            OwnedPagesRow  = info.OwnedPagesUmapRow,
            FreeSpaceRow   = info.FreeSpaceUmapRow,
            Indexes        = info.Indexes,
        };
    }

    /// <summary>
    /// Returns the raw bytes of row <paramref name="rowNum"/> from a data page,
    /// or <c>null</c> if the row is deleted or an overflow pointer.
    /// </summary>
    private static byte[]? ReadRowBytes(byte[] page, int rowNum, JetFormat format)
    {
        int slotValue = ByteUtil.GetUShort(page,
            format.OffsetDataRowTable + rowNum * JetFormat.SizeRowEntry);

        if ((slotValue & 0x8000) != 0) return null;   // deleted row
        if ((slotValue & 0x4000) != 0) return null;   // overflow pointer (not supported)

        int rowStart = slotValue & JetFormat.RowOffsetMask;

        // Row length: distance from this row's start to the previous row's start
        // (or end of page for row 0, which has the highest slot value = last appended).
        int rowEnd = (rowNum == 0)
            ? format.PageSize
            : (ByteUtil.GetUShort(page,
                   format.OffsetDataRowTable + (rowNum - 1) * JetFormat.SizeRowEntry)
               & JetFormat.RowOffsetMask);

        int rowLen = rowEnd - rowStart;
        if (rowLen <= 0) return null;

        var bytes = new byte[rowLen];
        Array.Copy(page, rowStart, bytes, 0, rowLen);
        return bytes;
    }

    // ── Obsolete stubs kept for API compat ────────────────────────────────────

    public void InsertTableEntry(string name)
        => throw new NotSupportedException(
               "Use InsertTableEntry(string tableName, int tdefPageNumber) instead.");

    public void InsertColumnEntry(string tableName, Column column)
        => throw new NotSupportedException(
               "Column metadata is stored directly in the table's TDEF page, " +
               "not in a separate system catalog table.");

    public void InsertIndexEntry(string tableName, string indexName, bool isPrimaryKey)
        => throw new NotSupportedException(
               "Index metadata is stored directly in the table's TDEF page, " +
               "not in a separate system catalog table.");
}
