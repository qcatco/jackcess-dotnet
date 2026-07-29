using System.Linq;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Holds the schema of a table plus the page-level state needed to write rows.
/// </summary>
public sealed class TableDefinition
{
    private const int     MagicTableNumber = 1625;
    private const byte    TableTypeUser    = 0x4E;

    private const byte    ColumnFlagFixedLen      = 0x01;
    private const byte    ColumnFlagUpdatable     = 0x02;
    /// <summary>General-legacy text sort order (LCID 1033) — what Access stamps on columns.</summary>
    private const short   GeneralLegacySortOrder  = 1033;
    private const byte    ColumnFlagAutoNumber    = 0x04;
    private const byte    ColumnFlagAutoNumberGuid= 0x40;

    // Per-index sort-flag bits (per Jackcess IndexData)
    private const byte    AscendingColumnFlag     = 0x01;
    // Per-index index-data-flags byte (per Jackcess IndexData)
    private const byte    UniqueIndexFlag         = 0x01;
    /// <summary>Index disallows null components — Access sets it on a primary key.</summary>
    private const byte    RequiredIndexFlag       = 0x08;
    private const byte    UnknownIndexFlag        = 0x80;   // always set on Access 2000+ indexes
    // Index slot's index-type byte
    private const byte    PrimaryKeyIndexType     = 0x01;
    // Sentinel meaning "no related index"
    private const int     InvalidIndexNumber      = -1;

    // ── Schema ────────────────────────────────────────────────────────────────
    public string               Name    { get; }
    public IReadOnlyList<Column> Columns { get; }

    // ── Page-level state (set by Database.CreateTable / GetTable) ────────────
    /// <summary>Page number that contains this table's TDEF.</summary>
    public int TdefPageNumber   { get; set; }
    /// <summary>Page number that contains the usage-map rows for this table.</summary>
    public int UmapPageNumber   { get; set; }
    /// <summary>Row index inside the umap page that holds the owned-pages bitmap.</summary>
    public int OwnedPagesRow    { get; set; }
    /// <summary>Row index inside the umap page that holds the free-space bitmap.</summary>
    public int FreeSpaceRow     { get; set; }

    // ── Primary-key index (optional) ─────────────────────────────────────────
    /// <summary>Page number of the primary key index leaf page (0 if no primary key index).</summary>
    public int     PrimaryKeyIndexPage   { get; set; }

    /// <summary>
    /// Row of <see cref="UmapPageNumber"/> holding the primary-key index's usage map, or -1
    /// when the table has no index. Rows 0 and 1 are the table's own owned-pages and
    /// free-space maps, so an index's map starts at row 2.
    /// </summary>
    public int     IndexUmapRow          { get; set; } = -1;
    /// <summary>
    /// Name of the primary key column for single-column PKs (null when there's
    /// no PK <i>or</i> the PK is composite). For composite PKs use
    /// <see cref="PrimaryKeyColumnNames"/>.
    /// </summary>
    public string? PrimaryKeyColumnName  { get; set; }
    /// <summary>
    /// Names of the PK columns for composite keys, in declaration order
    /// (max 10 entries — Jet's hard limit per index). Null/empty when the PK
    /// is single-column (see <see cref="PrimaryKeyColumnName"/>) or absent.
    /// </summary>
    public IReadOnlyList<string>? PrimaryKeyColumnNames { get; set; }

    /// <summary>
    /// Resolved list of PK column names. Empty when the table has no PK,
    /// single-element for the legacy single-column case, multi-element for
    /// composite keys.
    /// </summary>
    public IReadOnlyList<string> EffectivePrimaryKeyColumns =>
        PrimaryKeyColumnNames is { Count: > 0 } cols
            ? cols
            : PrimaryKeyColumnName is { } single
                ? new[] { single }
                : Array.Empty<string>();

    // ── Long-value column usage maps (one per Memo/OLE column) ───────────────
    /// <summary>
    /// Usage-map page numbers for long-value (Memo/OLE) columns, keyed by column name.
    /// Populated by <see cref="Database.CreateTable"/> before <see cref="Serialize"/> is called.
    /// </summary>
    public Dictionary<string, int> LvalColumnUmapPages { get; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Real B-tree indexes parsed from the on-disk TDEF (empty for tables this
    /// library creates, since we don't yet serialize real index column blocks).
    /// </summary>
    public IReadOnlyList<Index> Indexes { get; internal set; } = Array.Empty<Index>();

    public TableDefinition(string name, IReadOnlyList<Column> columns)
    {
        Name    = name    ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
    }

    // ── Serialisation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Serialises the table definition to a byte array that is ready to be written
    /// as a TDEF page.  The page is exactly <see cref="JetFormat.PageSize"/> bytes;
    /// unused trailing space is left as zero.
    ///
    /// Layout produced (Jet4):
    ///   [0..62]   TDEF header  (63 bytes)
    ///   [63..]    Column definitions  (numCols × 25 bytes)   ← no index blocks (0 indexes)
    ///   [..]      Column names        (2 + UTF-16LE per name)
    ///   [..]      Trailer             (0xFF 0xFF)
    /// </summary>
    public byte[] Serialize(JetFormat format)
    {
        if (format is null) throw new ArgumentNullException(nameof(format));
        if (Columns.Count == 0)
            throw new InvalidOperationException("Table must have at least one column.");
        if (Columns.Count > 255)
            throw new InvalidOperationException("Tables with more than 255 columns are not supported.");
        if (format.Version == JetVersion.Jet3)
            throw new NotSupportedException(
                "Writing Jet3 (Access 97) tables is not yet supported. " +
                "Jet3 column headers are 18 bytes with a different field layout than Jet4 (25 bytes).");

        // Compute column-name bytes (always UTF-16LE in TDEF)
        byte[][] nameBytes = Columns.Select(c => System.Text.Encoding.Unicode.GetBytes(c.Name)).ToArray();

        var pkColumns = EffectivePrimaryKeyColumns;
        if (pkColumns.Count > 10)
            throw new InvalidOperationException(
                $"Composite primary keys are limited to 10 columns; got {pkColumns.Count}.");

        bool hasPrimaryKey = pkColumns.Count > 0 && PrimaryKeyIndexPage > 0;
        int  numIndexes    = hasPrimaryKey ? 1 : 0;
        int  numIndexSlots = hasPrimaryKey ? 1 : 0;

        // Per-index sections sum to SizeIndexDefinition + SizeIndexColumnBlock + SizeIndexInfoBlock
        // for one index, plus a length-prefixed "PrimaryKey" name.
        byte[] pkNameBytes = hasPrimaryKey
            ? System.Text.Encoding.Unicode.GetBytes("PrimaryKey")
            : Array.Empty<byte>();

        int headerSize     = format.SizeTdefHeader;                  // 63 (Jet4) or 43 (Jet3)
        int idxDefSection  = numIndexes * format.SizeIndexDefinition;
        int colDefsSize    = format.SizeColumnHeader * Columns.Count;
        int nameSection    = nameBytes.Sum(b => 2 + b.Length);
        int idxColBlocks   = numIndexes * format.SizeIndexColumnBlock;
        int idxInfoBlocks  = numIndexSlots * format.SizeIndexInfoBlock;
        int idxNameSection = numIndexSlots * (2 + pkNameBytes.Length);
        int lvalSection    = Columns.Count(c => c.DataType.IsLongValue()) * 4;
        int trailerSize    = 2;

        int contentSize = headerSize + idxDefSection + colDefsSize + nameSection
                        + idxColBlocks + idxInfoBlocks + idxNameSection
                        + lvalSection + trailerSize;

        // Usually one page, but a table with enough columns has a longer definition than that —
        // Jet allows 255 — and it continues on further pages. The buffer covers the whole thing and
        // TdefChain lays it out across as many pages as it takes.
        var page = new byte[Math.Max(format.PageSize, contentSize)];

        // ── 8-byte page prefix ────────────────────────────────────────────────
        page[0] = JetFormat.PageTypeTableDef;
        page[1] = 0x01;
        ByteUtil.PutInt(page, 4, 0);   // next-TDEF-page = 0 (single-page table def)

        // Bytes 2-3 hold the page's remaining free space. Access accounts for it as
        // PageSize - 8 - definitionLength (an Access-written 1002-byte definition in a
        // 4096-byte page records 3086), so mirror that rather than leaving it zero.
        //
        // This has to come *after* the prefix bytes above. Written before them, the two zeroed
        // bytes that used to follow — a hand-written page[2] = page[3] = 0x00 — overwrote it, so
        // every definition this library produced recorded free space 0 while the code read as
        // though it recorded the real figure.
        //
        // The figure is the room left on the first page, so a definition that spills has none.
        int firstPageContent = Math.Min(contentSize, format.PageSize - 8);
        ByteUtil.PutShort(page, 2, (short)(format.PageSize - 8 - firstPageContent));

        int pos = 8;

        // ── TDEF header (varies by format) ────────────────────────────────────
        ByteUtil.PutInt  (page, pos, contentSize); pos += 4;  // total content size
        ByteUtil.PutInt  (page, pos, MagicTableNumber); pos += 4;
        ByteUtil.PutInt  (page, pos, 0); pos += 4;   // num rows (initially 0)
        ByteUtil.PutInt  (page, pos, 0); pos += 4;   // last autonumber
        page[pos++] = 0x01;                           // autonumber flag

        // SizeTdefHeader is measured from byte 0 of the page, so we subtract
        // the current absolute write position (pos) plus the size of all
        // remaining fixed fields that follow the unknown padding.
        int unknownPad = format.SizeTdefHeader - pos
                         - 1  /* table type */
                         - 2  /* max cols */
                         - 2  /* var cols */
                         - 2  /* num cols */
                         - 4  /* logical index count */
                         - 4  /* real index count */
                         - 4  /* owned-pages umap ref  (1 row byte + 3 page bytes) */
                         - 4  /* free-space umap ref   (1 row byte + 3 page bytes) */;
        for (int i = 0; i < unknownPad; i++) page[pos++] = 0x00;

        page[pos++] = TableTypeUser;
        ByteUtil.PutShort(page, pos, (short)Columns.Count); pos += 2;  // max cols
        ByteUtil.PutShort(page, pos, (short)Columns.Count(c => c.DataType.IsVariableLength())); pos += 2;
        ByteUtil.PutShort(page, pos, (short)Columns.Count); pos += 2;  // col count
        ByteUtil.PutInt  (page, pos, numIndexSlots); pos += 4;   // logical index count
        ByteUtil.PutInt  (page, pos, numIndexes);    pos += 4;   // real index count

        // Owned-pages umap reference: row 0 of UmapPageNumber
        page[pos++] = (byte)OwnedPagesRow;
        ByteUtil.Put3ByteInt(page, pos, UmapPageNumber); pos += 3;

        // Free-space umap reference: row 1 of UmapPageNumber
        page[pos++] = (byte)FreeSpaceRow;
        ByteUtil.Put3ByteInt(page, pos, UmapPageNumber); pos += 3;

        // pos should now == SizeTdefHeader (the header size includes the 8-byte page prefix)
        pos = headerSize;

        // ── Index row-count blocks (zeros — Jackcess does the same) ──────────
        pos += idxDefSection;

        // ── Assign column layout ──────────────────────────────────────────────
        var fixedOffsets = new Dictionary<Column, short>();
        var varIndexes   = new Dictionary<Column, short>();
        short fixedOff = 0;
        short varIdx   = 0;

        for (short i = 0; i < Columns.Count; i++)
        {
            Columns[i].ColumnNumber = i;
            if (Columns[i].DataType.IsVariableLength())
                varIndexes[Columns[i]] = varIdx++;
            else
            {
                fixedOffsets[Columns[i]] = fixedOff;
                fixedOff += (short)Columns[i].Length;
            }
        }

        // ── Column definitions (25 bytes each) ───────────────────────────────
        // The variable-length-table index field carries the *running* counter for every
        // column, not just the variable-length ones: a fixed column stores the index the
        // next variable column will take. Access writes it that way, and writing 0 on
        // fixed columns leaves it unable to read the table's rows at all (it reports
        // "Not a valid bookmark") even though the row bytes themselves are fine.
        short varCounter = 0;

        foreach (var col in Columns)
        {
            page[pos++] = (byte)col.DataType;
            ByteUtil.PutInt  (page, pos, MagicTableNumber); pos += 4;
            ByteUtil.PutShort(page, pos, (short)col.ColumnNumber); pos += 2;
            ByteUtil.PutShort(page, pos, varCounter); pos += 2;
            if (col.DataType.IsVariableLength()) varCounter++;
            ByteUtil.PutShort(page, pos, (short)col.ColumnNumber); pos += 2;

            // These two bytes are precision+scale for Numeric and the text sort order for
            // everything else — Access stamps the general-legacy LCID on every other type,
            // including fixed numeric ones like Long and Money.
            if (col.DataType == DataType.Numeric)
            {
                page[pos++] = col.Precision;
                page[pos++] = col.Scale;
            }
            else
            {
                ByteUtil.PutShort(page, pos, GeneralLegacySortOrder); pos += 2;
            }
            for (int i = 2; i < format.SizeSortOrder; i++) page[pos++] = 0x00;

            byte flags = ColumnFlagUpdatable;
            if (!col.DataType.IsVariableLength()) flags |= ColumnFlagFixedLen;
            if (col.IsAutoNumber)
                flags |= (col.DataType == DataType.Guid ? ColumnFlagAutoNumberGuid : ColumnFlagAutoNumber);

            page[pos++] = flags;
            // ext flags: bit 0x01 declares the column's text as compressed-unicode, which
            // is what lets Access recognise the 0xFF 0xFE header the encoder writes.
            page[pos++] = col.IsCompressedUnicode ? (byte)0x01 : (byte)0x00;
            ByteUtil.PutInt  (page, pos, 0); pos += 4;
            ByteUtil.PutShort(page, pos, col.DataType.IsVariableLength() ? (short)0 : fixedOffsets[col]); pos += 2;
            ByteUtil.PutShort(page, pos, col.DataType.IsLongValue()      ? (short)0 : (short)col.Length); pos += 2;
        }

        // ── Column names (length-prefixed UTF-16LE) ───────────────────────────
        for (int i = 0; i < Columns.Count; i++)
        {
            ByteUtil.PutShort(page, pos, (short)nameBytes[i].Length); pos += 2;
            Array.Copy(nameBytes[i], 0, page, pos, nameBytes[i].Length);
            pos += nameBytes[i].Length;
        }

        // ── Index column block(s) + slot info + index name ────────────────────
        // Only emitted when PrimaryKey is configured. Layout per Jackcess Java:
        //   index column block: SkipBeforeIndex + 10×(short colNum + byte flags)
        //                     + 4-byte umap ref + 4-byte root page
        //                     + SkipBeforeIndexFlags + 1-byte flags + SkipAfterIndexFlags
        //   slot block:        SkipBeforeIndexSlot + 4-byte indexNumber + 4-byte indexDataNumber
        //                     + 1-byte relIndexType + 4-byte relIndexNumber + 4-byte relTablePage
        //                     + 1-byte cascadeUpdates + 1-byte cascadeDeletes + 1-byte indexType
        //                     + SkipAfterIndexSlot
        //   slot name:         length-prefixed UTF-16LE
        if (hasPrimaryKey)
        {
            // Map every PK column name → its column number; throw if any is missing.
            short[] pkColumnNumbers = pkColumns.Select(name =>
            {
                var col = Columns.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (col is null)
                    throw new InvalidOperationException(
                        $"Primary key column '{name}' not found in table '{Name}'.");
                return (short)col.ColumnNumber;
            }).ToArray();

            int blockStart = pos;
            int p = pos + format.SkipBeforeIndex;

            // 10 column slots: populated PK columns ASC, remaining slots = COLUMN_UNUSED (-1).
            for (int c = 0; c < 10; c++)
            {
                if (c < pkColumnNumbers.Length)
                {
                    ByteUtil.PutShort(page, p, pkColumnNumbers[c]); p += 2;
                    page[p++] = AscendingColumnFlag;
                }
                else
                {
                    ByteUtil.PutShort(page, p, unchecked((short)0xFFFF)); p += 2;
                    page[p++] = 0x00;
                }
            }
            // Usage-map reference: 1-byte row + 3-byte page. An index has its own map, kept as
            // a further row of the table's usage-map page. Leaving this blank is what made
            // Access reject an indexed table outright ("Not a valid bookmark") — it could not
            // find the index's page map, however few rows the table had.
            page[p++] = (byte)(IndexUmapRow >= 0 ? IndexUmapRow : 0);
            ByteUtil.Put3ByteInt(page, p, IndexUmapRow >= 0 ? UmapPageNumber : 0); p += 3;

            // Root page = PK index leaf page.
            ByteUtil.PutInt(page, p, PrimaryKeyIndexPage); p += 4;
            p += format.SkipBeforeIndexFlags;
            // A primary key is unique *and* required — Access writes 0x89 here, and we were
            // writing 0x81.
            page[p++] = (byte)(UnknownIndexFlag | UniqueIndexFlag | RequiredIndexFlag);
            // SkipAfterIndexFlags absorbed by the block-size advance below.
            pos = blockStart + format.SizeIndexColumnBlock;

            // Logical-index slot block. Access and Jackcess both open it with the same magic
            // table number the column definitions carry. Leaving those four bytes zero still
            // allowed seeks and range scans through the index, but Access could not answer
            // COUNT(*) or MAX(...) from it — those failed with "Invalid argument".
            int slotStart = pos;
            if (format.SkipBeforeIndexSlot >= 4)
                ByteUtil.PutInt(page, slotStart, MagicTableNumber);
            p = pos + format.SkipBeforeIndexSlot;
            ByteUtil.PutInt(page, p, 0);                   p += 4;   // indexNumber
            ByteUtil.PutInt(page, p, 0);                   p += 4;   // indexDataNumber
            p += 1;                                                 // relIndexType
            ByteUtil.PutInt(page, p, InvalidIndexNumber);  p += 4;   // relIndexNumber
            p += 4;                                                 // relTablePage
            p += 2;                                                 // cascadeUpdates + cascadeDeletes
            page[p] = PrimaryKeyIndexType;
            pos = slotStart + format.SizeIndexInfoBlock;

            // Length-prefixed "PrimaryKey" name.
            ByteUtil.PutShort(page, pos, (short)pkNameBytes.Length); pos += 2;
            Array.Copy(pkNameBytes, 0, page, pos, pkNameBytes.Length);
            pos += pkNameBytes.Length;
        }

        // ── LVAL column usage-map references (4 bytes each: 1-byte row + 3-byte page) ──
        // One entry per long-value (Memo/OLE) column, in column-number order.
        foreach (var col in Columns.Where(c => c.DataType.IsLongValue()))
        {
            LvalColumnUmapPages.TryGetValue(col.Name, out int lvalPage);
            page[pos++] = 0;                                  // row 0 = owned-pages bitmap
            ByteUtil.Put3ByteInt(page, pos, lvalPage); pos += 3;
        }

        // ── Trailer ───────────────────────────────────────────────────────────
        page[pos++] = 0xFF;
        page[pos]   = 0xFF;

        return page;
    }
}
