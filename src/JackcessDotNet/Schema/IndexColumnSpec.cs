namespace JackcessDotNet;

/// <summary>
/// One column of an index being created, and the direction it sorts in.
/// <para>
/// Ascending is the default and by far the common case; a descending column's key bytes are stored
/// inverted, which is how a single byte-wise comparison walks part of a composite key backwards.
/// </para>
/// </summary>
/// <param name="Column">Name of the table column to index.</param>
/// <param name="Ascending">False to sort this column descending.</param>
public readonly record struct IndexColumnSpec(string Column, bool Ascending = true)
{
    /// <summary>Lets a plain column name stand in wherever a spec is expected.</summary>
    public static implicit operator IndexColumnSpec(string column) => new(column);

    public override string ToString() => Ascending ? Column : $"{Column} DESC";
}
