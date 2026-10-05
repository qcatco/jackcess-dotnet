namespace JackcessDotNet;

/// <summary>
/// A value longer than its column allows, refused before anything of its row is written - as Access refuses one.
/// Its own type, so a caller can tell it from any other argument error and say which record held the value.
/// </summary>
public sealed class ColumnValueTooLongException : ArgumentException
{
    public ColumnValueTooLongException(string column, int maxLength, int length)
        : base($"Column '{column}' holds at most {maxLength} characters; the value has {length}.")
    {
        Column = column;
        MaxLength = maxLength;
        Length = length;
    }

    /// <summary>The column's name.</summary>
    public string Column { get; }

    /// <summary>The most characters the column holds.</summary>
    public int MaxLength { get; }

    /// <summary>The characters the value has.</summary>
    public int Length { get; }
}
