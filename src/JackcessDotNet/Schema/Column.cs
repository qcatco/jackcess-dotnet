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

    /// <summary>
    /// Whether a Text or Memo value may be written in Jet's compressed form (0xFF 0xFE,
    /// then one byte per character): the column's "Unicode Compression" setting, read
    /// from its TDEF. Access reads a value in that form as UTF-16 in a column without
    /// it, so MSysObjects' names, for one, are written uncompressed. Columns this
    /// library creates allow it, as Access's own new Text and Memo fields do.
    /// </summary>
    public bool IsCompressedUnicode { get; internal set; } = true;

    /// <summary>
    /// The order a Text or Memo column's values sort in, in its indexes, read from its TDEF. Columns this library
    /// creates sort in General - Legacy, the order whose keys it writes.
    /// </summary>
    internal SortOrder SortOrder { get; set; } = SortOrder.GeneralLegacy;

    internal Column(
        string name,
        DataType dataType,
        int length,
        bool isRequired,
        bool isAutoNumber,
        bool allowZeroLength,
        byte precision,
        byte scale)
    {
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
