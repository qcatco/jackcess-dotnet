namespace JackcessDotNet;

/// <summary>
/// Read-only metadata for a real (B-tree) index defined on a table.
///
/// Each entry in <see cref="Columns"/> participates in the composite sort key,
/// in declaration order. Index trees rooted at <see cref="RootPageNumber"/> are
/// not yet traversed by this library for Access-authored files (see B-step2/3);
/// the metadata is exposed today so callers can introspect schemas and so the
/// future B-tree walker has the entry points it needs.
/// </summary>
public sealed class Index
{
    /// <summary>Index name (e.g. "PrimaryKey", "ByLastName"); never null.</summary>
    public string Name { get; }

    /// <summary>Columns participating in the sort key, in declaration order.</summary>
    public IReadOnlyList<IndexColumn> Columns { get; }

    /// <summary>
    /// Page number of the index's root page (node or leaf). Updated in place when an insert
    /// splits the root and a new level is promoted above it, so an in-memory definition stays
    /// usable after the TDEF has been patched.
    /// </summary>
    public int RootPageNumber { get; internal set; }

    /// <summary>
    /// The slot's logical index number — what a foreign-key reference in another table's
    /// TDEF points at. This is <em>not</em> a position in any array; see
    /// <see cref="IndexDataNumber"/> for the one that is.
    /// </summary>
    public int IndexNumber { get; }

    /// <summary>
    /// Which index-data block in the TDEF holds this index's tree: the position of its
    /// row-count block (in the section before the columns) and of its column block (in the
    /// section after them). Several logical indexes may share one data block, and the slot
    /// order need not match the block order — Access commonly writes the primary key as the
    /// last slot while its tree lives in the first block. Anything that seeks a per-index
    /// field inside the TDEF must offset by this, never by the index's position in
    /// <see cref="TableDefinition.Indexes"/>.
    /// </summary>
    public int IndexDataNumber { get; }

    /// <summary>Raw index-flags byte from the index column block.</summary>
    public byte Flags { get; }

    /// <summary>Index type byte from the logical-index slot (1 = primary key, 2 = foreign key, 0 = regular).</summary>
    public byte IndexType { get; }

    public bool IsPrimaryKey => IndexType == 1;
    public bool IsForeignKey => IndexType == 2;
    public bool IsUnique     => IsPrimaryKey || (Flags & 0x01) != 0;
    public bool IgnoresNulls => (Flags & 0x02) != 0;
    public bool IsRequired   => (Flags & 0x08) != 0;

    internal Index(string name, IReadOnlyList<IndexColumn> columns, int rootPageNumber,
                   int indexNumber, byte flags, byte indexType, int indexDataNumber = 0)
    {
        Name            = name;
        Columns         = columns;
        RootPageNumber  = rootPageNumber;
        IndexNumber     = indexNumber;
        Flags           = flags;
        IndexType       = indexType;
        // Only the TDEF reader builds real index metadata, and it always knows the block; the
        // default covers the single-index definitions tests hand-assemble, where slot 0's tree
        // is necessarily block 0.
        IndexDataNumber = indexDataNumber;
    }

    public override string ToString()
        => $"{Name} [{IndexType switch { 1 => "PK", 2 => "FK", _ => "IDX" }}] " +
           $"on ({string.Join(", ", Columns)}) root=p{RootPageNumber}";
}

/// <summary>
/// One column participating in an index's composite sort key.
/// </summary>
public sealed class IndexColumn
{
    /// <summary>The table column being indexed.</summary>
    public Column Column { get; }

    /// <summary>Raw flags byte from the index column block.</summary>
    public byte Flags { get; }

    /// <summary>True when the column is sorted ascending in this index (bit 0 of <see cref="Flags"/>).</summary>
    public bool IsAscending => (Flags & 0x01) != 0;

    internal IndexColumn(Column column, byte flags)
    {
        Column = column;
        Flags  = flags;
    }

    public override string ToString()
        => $"{Column.Name}{(IsAscending ? " ASC" : " DESC")}";
}
