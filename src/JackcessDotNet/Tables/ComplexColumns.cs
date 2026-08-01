namespace JackcessDotNet;

/// <summary>
/// Resolves the values behind a complex column — an Access 2007+ multi-value field,
/// attachment field, or append-only (version history) memo.
///
/// The row itself only carries a 4-byte id. The values live in a per-column "flat" table
/// (named <c>f_&lt;guid&gt;_&lt;column&gt;</c>) whose rows point back at that id through a column
/// named <c>&lt;OwningTable&gt;_&lt;ColumnName&gt;</c>. <c>MSysComplexColumns</c> ties the two
/// together:
/// <list type="bullet">
///   <item><c>ColumnName</c> — the complex column on the owning table</item>
///   <item><c>ConceptualTableID</c> — the owning table's object id</item>
///   <item><c>FlatTableID</c> — the object id of the table holding the values</item>
/// </list>
/// Read-only: complex values cannot be written yet.
/// </summary>
internal static class ComplexColumns
{
    private const string CatalogTable = "MSysComplexColumns";

    /// <summary>
    /// Returns the flat table holding <paramref name="columnName"/>'s values, or null when the
    /// database has no complex-column catalog or no entry for this column.
    /// </summary>
    internal static Table? ResolveFlatTable(Database db, int owningTableObjectId, string columnName)
    {
        IReadOnlyList<Row> catalog;
        try { catalog = db.GetTable(CatalogTable).ReadAllRows(); }
        catch { return null; }   // no MSysComplexColumns → not a complex-capable database

        foreach (Row entry in catalog)
        {
            if (entry.TryGetValue("ColumnName", out object? name) is false) continue;
            if (!string.Equals(name as string, columnName, StringComparison.OrdinalIgnoreCase)) continue;

            // A database can hold the same column name on several tables, so the owning
            // table has to match too — otherwise the wrong field's values come back.
            if (entry.TryGetValue("ConceptualTableID", out object? conceptual)
                && conceptual is int conceptualId
                && conceptualId != owningTableObjectId)
                continue;

            if (!entry.TryGetValue("FlatTableID", out object? flat) || flat is not int flatId)
                continue;

            string? flatName = ResolveTableName(db, flatId);
            if (flatName is null) continue;

            try { return db.GetTable(flatName); } catch { return null; }
        }

        return null;
    }

    /// <summary>Maps an MSysObjects object id back to its table name.</summary>
    private static string? ResolveTableName(Database db, int objectId)
    {
        foreach (Row row in db.GetTable("MSysObjects").ReadAllRows())
            if (row.TryGetValue("Id", out object? id) && id is int rowId && rowId == objectId
                && row.TryGetValue("Name", out object? name))
                return name as string;
        return null;
    }

    /// <summary>
    /// The flat-table column that points back at the owning row's complex id. Access names it
    /// <c>&lt;OwningTable&gt;_&lt;ColumnName&gt;</c>; the leading <c>_&lt;ColumnName&gt;</c>
    /// column carries the same value, so either identifies the group.
    /// </summary>
    internal static Column? FindBackReference(Table flatTable, string owningTableName, string columnName)
    {
        string bare      = $"_{columnName}";
        string qualified = $"{owningTableName}_{columnName}";

        // The bare "_<column>" holds the owning row's complex id; the table-qualified twin holds
        // the value's own id within the column. Preferring the qualified one matched anyway while
        // both were being read from the same wrong offset, and broke as soon as they decoded
        // apart — the multi-value row whose owner is 2 has _multi-value-data = 2 and
        // Table1_multi-value-data = 1.
        return flatTable.Columns.FirstOrDefault(c => c.Name.Equals(bare,      StringComparison.OrdinalIgnoreCase))
            ?? flatTable.Columns.FirstOrDefault(c => c.Name.Equals(qualified, StringComparison.OrdinalIgnoreCase));
    }
}
