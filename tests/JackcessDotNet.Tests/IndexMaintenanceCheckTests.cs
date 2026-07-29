using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Covers the guard around index maintenance. An ordinary leaf split is written correctly and
/// goes through silently; what the writer still refuses is a split it would have to absorb by
/// splitting the <em>root node</em> as well, i.e. growing a third level. Rather than damage an
/// index the insert throws — unless the caller opts out with
/// <see cref="Table.ForceIgnoreIndexCheck"/>, which keeps inserting and leaves that index
/// without the new entries.
/// </summary>
public sealed class IndexMaintenanceCheckTests : IDisposable
{
    private readonly string _path;
    public IndexMaintenanceCheckTests()
        => _path = Path.Combine(Path.GetTempPath(), $"idxchk_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // What fills a page is the size of the *index key*, not of the row, so the two shapes below
    // differ in the width of the indexed column: a 200-char text key gives ~7 entries per leaf and
    // ~7 children per node, so the root node fills within ~50 inserts, while a 4-byte Long key
    // gives ~175 per leaf and would need tens of thousands of rows to fill a node.

    /// <summary>Wide index keys: the root node fills within ~50 inserts.</summary>
    private static IReadOnlyList<Column> WideKeyColumns() => new[]
    {
        new ColumnBuilder("Key", DataType.Text).MaxLength(200).Build(),
        new ColumnBuilder("Id",  DataType.Long).Build(),
    };

    /// <summary>Narrow index keys: leaves split long before the root node fills.</summary>
    private static IReadOnlyList<Column> NarrowKeyColumns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(10).Build(),
    };

    private static Row WideRow(int i)   => new() { ["Key"] = $"{i:D6}" + new string('x', 190), ["Id"] = i };
    private static Row NarrowRow(int i) => new() { ["Id"] = i, ["Name"] = $"n{i}" };

    /// <summary>
    /// Creates the table, then reopens the database so the TDEF's index blocks are read back into
    /// <see cref="Table.Indexes"/> — that is what makes the general index path (and therefore the
    /// guard) apply, rather than the primary-key-only path a freshly created table uses.
    /// </summary>
    private Database CreateAndReopen(IReadOnlyList<Column> columns, string primaryKey)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", columns, primaryKey: primaryKey);
        return Database.Open(_path);
    }

    /// <summary>Inserts until the guard fires, returning how many rows landed.</summary>
    private static int FillUntilRefused(Table table, int limit, out NotSupportedException? refusal)
    {
        refusal = null;
        for (int i = 0; i < limit; i++)
        {
            try { table.Insert(WideRow(i)); }
            catch (NotSupportedException ex) { refusal = ex; return i; }
        }
        return limit;
    }

    [Fact]
    public void ALeafSplit_IsNotRefused_AndEveryRowStaysFindable()
    {
        const int Rows = 600;

        using var db = CreateAndReopen(NarrowKeyColumns(), primaryKey: "Id");
        var table = db.GetTable("T");

        // No try/catch: a leaf split must not throw. Only a node split is unsupported, and these
        // rows are narrow enough that the root node still has room after several leaf splits.
        for (int i = 0; i < Rows; i++) table.Insert(NarrowRow(i));

        var reread = db.GetTable("T");
        Assert.Equal(Rows, reread.ReadAllRows().Count);

        // The tree is deeper than one page by now, so these seeks exercise node descent.
        for (int i = 0; i < Rows; i++)
            Assert.NotNull(reread.NewIndexCursor().FindRowByPrimaryKey(i));
    }

    [Fact]
    public void ByDefault_AnInsertThatWouldSplitTheRootNode_IsRefused()
    {
        using var db = CreateAndReopen(WideKeyColumns(), primaryKey: "Key");
        var table = db.GetTable("T");
        Assert.False(table.ForceIgnoreIndexCheck);

        int written = FillUntilRefused(table, 1200, out var refusal);

        Assert.NotNull(refusal);
        Assert.Contains("root node", refusal!.Message);
        Assert.Contains("ForceIgnoreIndexCheck", refusal.Message);
        Assert.True(written > 0, "the guard should only fire once the root node is actually full");

        // The refused row must not be there, and everything written before it must be.
        Assert.Equal(written, db.GetTable("T").ReadAllRows().Count);
    }

    [Fact]
    public void WithForceIgnoreIndexCheck_InsertsKeepGoingPastTheRefusalPoint()
    {
        using var db = CreateAndReopen(WideKeyColumns(), primaryKey: "Key");
        var guarded = db.GetTable("T");
        int refusedAt = FillUntilRefused(guarded, 1200, out var refusal);
        Assert.NotNull(refusal);

        var forced = db.GetTable("T");
        forced.ForceIgnoreIndexCheck = true;
        for (int i = refusedAt; i < refusedAt + 20; i++) forced.Insert(WideRow(i));

        // Rows are all present and readable; only the index entries are missing.
        Assert.Equal(refusedAt + 20, db.GetTable("T").ReadAllRows().Count);
    }

    [Fact]
    public void ImportOptions_CarriesTheOverrideOntoTheTable()
    {
        using var db = CreateAndReopen(WideKeyColumns(), primaryKey: "Key");
        var table = db.ImportTable(
            new[] { new { Key = "a", Id = 1 } },
            tableName: "T",
            options: new ImportOptions { AppendIfExists = true, ForceIgnoreIndexCheck = true });

        Assert.True(table.ForceIgnoreIndexCheck);
    }
}
