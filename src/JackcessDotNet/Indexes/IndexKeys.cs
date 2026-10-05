namespace JackcessDotNet;

/// <summary>
/// The bytes an index entry's key is made of, as Access writes them: for each
/// column of the index, a flag (0x7F ascending, 0x80 descending; 0x00 or 0xFF
/// for a null) and then the value's bytes, each column with its own flag. The
/// value's bytes follow the column's type, whatever CLR type the value arrives
/// as: a Long column's key is four bytes even for a value passed as a short.
/// </summary>
internal static class IndexKeys
{
    private const byte AscStartFlag  = 0x7F;
    private const byte DescStartFlag = 0x80;
    private const byte AscNullFlag   = 0x00;
    private const byte DescNullFlag  = 0xFF;

    /// <summary>True for the column types <see cref="Encode"/> can write a key for.</summary>
    public static bool CanEncode(DataType type)
        => type is DataType.Byte or DataType.Int or DataType.Long or DataType.Text or DataType.Guid;

    /// <summary>
    /// True when <see cref="Encode"/> writes the keys Access writes for <paramref name="column"/>: a type it
    /// encodes and, for text, a column that sorts in General - Legacy, the order of the text keys it writes.
    /// </summary>
    public static bool CanEncode(Column column)
        => CanEncode(column.DataType) && (column.DataType != DataType.Text || column.SortOrder.IsGeneralLegacy);

    /// <summary>Why <paramref name="column"/>'s keys cannot be written (<see cref="CanEncode(Column)"/> is false).</summary>
    public static string WhyNot(Column column)
        => CanEncode(column.DataType)
            ? $"{column.Name} sorts in {column.SortOrder}, and this library writes text keys only in {SortOrder.GeneralLegacy}"
            : $"{column.Name} is a {column.DataType} column, whose keys this library does not write";

    /// <summary>The key of an entry whose columns hold <paramref name="values"/>, in the index's column order.</summary>
    public static byte[] Encode(IReadOnlyList<IndexColumn> columns, IReadOnlyList<object?> values)
    {
        if (values.Count != columns.Count)
            throw new ArgumentException($"The index has {columns.Count} columns; {values.Count} values were given.", nameof(values));
        var key = new List<byte>();
        for (int i = 0; i < columns.Count; i++)
            key.AddRange(EncodeColumn(columns[i].Column.DataType, columns[i].IsAscending, values[i]));
        return key.ToArray();
    }

    /// <summary>One column's part of a key: its flag, then its value's bytes.</summary>
    public static byte[] EncodeColumn(DataType type, bool ascending, object? value)
    {
        if (value is null)
            return new[] { ascending ? AscNullFlag : DescNullFlag };

        byte[] bytes;
        switch (type)
        {
            case DataType.Text:
                // The text encoder writes its own end markers and does its own
                // flipping for a descending column.
                bytes = GeneralLegacyIndexCodes.EncodeText(Convert.ToString(value) ?? string.Empty, ascending);
                return Prepend(ascending ? AscStartFlag : DescStartFlag, bytes);
            case DataType.Byte:
                bytes = new[] { Convert.ToByte(value) };
                break;
            case DataType.Int:
                bytes = SignFlipped(BigEndian(Convert.ToInt16(value)));
                break;
            case DataType.Long:
                bytes = SignFlipped(BigEndian(Convert.ToInt32(value)));
                break;
            case DataType.Guid:
                // Big-endian, in 8-byte segments each followed by a length byte:
                // 9 for a segment with more after it, 8 for the last.
                byte[] g = BigEndian(value is Guid guid ? guid : Guid.Parse(Convert.ToString(value)!));
                bytes = new byte[18];
                Array.Copy(g, 0, bytes, 0, 8);
                bytes[8] = 9;
                Array.Copy(g, 8, bytes, 9, 8);
                bytes[17] = 8;
                if (!ascending)
                {
                    // The length byte of a segment with more after it stays as it is.
                    for (int i = 0; i < bytes.Length; i++)
                        if (i != 8) bytes[i] = (byte)~bytes[i];
                }
                return Prepend(ascending ? AscStartFlag : DescStartFlag, bytes);
            default:
                throw new NotSupportedException($"Index keys on {type} columns are not supported.");
        }
        if (!ascending)
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)~bytes[i];
        return Prepend(ascending ? AscStartFlag : DescStartFlag, bytes);
    }

    private static byte[] Prepend(byte flag, byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        result[0] = flag;
        Array.Copy(bytes, 0, result, 1, bytes.Length);
        return result;
    }

    private static byte[] SignFlipped(byte[] bigEndian)
    {
        bigEndian[0] ^= 0x80;
        return bigEndian;
    }

    private static byte[] BigEndian(short v) => new[] { (byte)(v >> 8), (byte)v };

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    // A GUID's bytes in the order its text shows them (.NET's own array keeps the
    // first three groups little-endian).
    private static byte[] BigEndian(Guid g)
    {
        byte[] b = g.ToByteArray();
        return new[] { b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15] };
    }
}
