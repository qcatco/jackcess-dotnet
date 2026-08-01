namespace JackcessDotNet;

/// <summary>
/// Describes a column in an Access table.
/// </summary>
public sealed class Column
{
    /// <summary>Column name (max 64 characters in Jet 4).</summary>
    public string Name { get; }

    /// <summary>Jet data type.</summary>
    public DataType DataType { get; }

    /// <summary>
    /// Storage length in bytes.
    /// For Text: max bytes = max chars * 2 (UTF-16LE).
    /// For Binary: max bytes directly.
    /// For fixed-length types: matches <see cref="DataType.GetFixedSize()"/>.
    /// </summary>
    public int Length { get; }

    /// <summary>Column ordinal position (0-based).</summary>
    public int ColumnNumber { get; internal set; }

    /// <summary>Whether the column is required (NOT NULL).</summary>
    public bool IsRequired { get; }

    /// <summary>Whether this is an AutoNumber (identity) column.</summary>
    public bool IsAutoNumber { get; }

    /// <summary>Whether this column allows zero-length strings (Text/Memo only).</summary>
    public bool AllowZeroLength { get; }

    /// <summary>Precision for Numeric type (1-28).</summary>
    public byte Precision { get; }

    /// <summary>Scale for Numeric type (0-28).</summary>
    public byte Scale { get; }

    /// <summary>
    /// Byte offset of this fixed-length column inside the row's fixed-data area,
    /// as stored in the TDEF. -1 for variable-length columns or when the value
    /// hasn't been populated by the reader.
    ///
    /// This matters for tables that have had columns deleted: Access leaves the
    /// deleted column's slot in row bytes, so subsequent columns' actual offsets
    /// aren't a tight pack — they must be read from the TDEF.
    /// </summary>
    public short FixedDataOffset { get; internal set; } = -1;

    /// <summary>0-based index into the variable-length offset table; -1 for fixed columns.</summary>
    public short VarLenTableIndex { get; internal set; } = -1;

    private bool? _variableLengthStorage;

    /// <summary>
    /// Whether this column's value lives in the row's variable-length area rather than at a fixed
    /// offset.
    /// <para>
    /// For a column read off disk this is the column's own fixed-length flag, not something its
    /// type implies: Access stores some numeric columns as variable-length — the foreign key of a
    /// complex column's flat table is a Long stored that way — and reading one at a fixed offset
    /// picks up whatever happens to sit there. A column built in memory for a table being created
    /// has no flag yet, so its type decides, which is what the writer then records.
    /// </para>
    /// </summary>
    public bool IsVariableLengthStorage => _variableLengthStorage ?? DataType.IsVariableLength();

    /// <summary>Records the storage class read from the column's on-disk flags.</summary>
    internal void SetStorageFromFlags(bool isFixedLength) => _variableLengthStorage = !isFixedLength;

    /// <summary>
    /// Copies <paramref name="other"/>'s storage class onto this column. The reader rebuilds a
    /// column once its name is known, and anything not carried across is silently lost.
    /// </summary>
    internal Column WithStorageOf(Column other)
    {
        _variableLengthStorage = other._variableLengthStorage;
        return this;
    }

    /// <summary>
    /// Whether this Text/Memo column stores values in Jet's compressed form — bit 0x01 of
    /// the column's ext-flags byte in the TDEF.
    /// <para>
    /// A value may only be written compressed when this is set: Access looks for the
    /// 0xFF 0xFE header only on a flagged column, and reads the value as UTF-16 otherwise,
    /// which turns Latin-1 text into byte-paired nonsense. Read from the file for existing
    /// tables, so appending honours whatever the file already declares.
    /// </para>
    /// </summary>
    public bool IsCompressedUnicode { get; internal set; }

    internal Column(
        string name,
        DataType dataType,
        int length,
        bool isRequired,
        bool isAutoNumber,
        bool allowZeroLength,
        byte precision,
        byte scale,
        bool isCompressedUnicode = false)
    {
        IsCompressedUnicode = isCompressedUnicode;
        Name = name;
        DataType = dataType;
        Length = length;
        IsRequired = isRequired;
        IsAutoNumber = isAutoNumber;
        AllowZeroLength = allowZeroLength;
        Precision = precision;
        Scale = scale;
    }

    public override string ToString() => $"{Name} ({DataType}, {Length})";
}
