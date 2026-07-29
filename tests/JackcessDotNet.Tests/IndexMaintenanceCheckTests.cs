using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The guard around index maintenance, which now has almost nothing left to refuse: leaf splits,
/// pages Access prefix-compressed, and full nodes are all written correctly, so inserts that used
/// to throw go through. <see cref="Table.ForceIgnoreIndexCheck"/> survives as public API but no
/// longer changes what happens to any index this library can reach.
/// </summary>
public sealed class IndexMaintenanceCheckTests : IDisposable
{
    private readonly string _path;
    public IndexMaintenanceCheckTests()
        => _path = Path.Combine(Path.GetTempPath(), $"idxchk_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // What fills a page is the size of the index *key*, not the row. A 200-char text key gives
    // ~7 entries per leaf and ~7 children per node, so the root node fills within ~50 rows — the
    // point at which inserting used to throw.
    private static IReadOnlyList<Column> WideKeyColumns() => new[]
    {
        new ColumnBuilder("Key", DataType.Text).MaxLength(200).Build(),
        new ColumnBuilder("Id",  DataType.Long).Build(),
    };

    private static string KeyFor(int i) => $"{i:D6}" + new string('x', 190);
    private static Row    RowFor(int i) => new() { ["Key"] = KeyFor(i), ["Id"] = i };

    /// <summary>
    /// Creates the table, then reopens the database so the TDEF's index blocks are read back into
    /// <see cref="Table.Indexes"/> — that is what makes the general index path apply, rather than
    /// the primary-key-only path a freshly created table uses.
    /// </summary>
    private Database CreateAndReopen()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", WideKeyColumns(), primaryKey: "Key");
        return Database.Open(_path);
    }

    [Fact]
    public void WideKeysPastTheOldRefusalPoint_InsertWithoutThrowing()
    {
        // ~24× what a two-level tree of these keys holds, so this drives several node splits.
        const int Rows = 1200;

        using var db = CreateAndReopen();
        var table = db.GetTable("T");
        Assert.False(table.ForceIgnoreIndexCheck);

        // No try/catch: none of these may throw. Before node splits were written, the insert at
        // roughly row 50 threw NotSupportedException.
        for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));

        Assert.Equal(Rows, db.GetTable("T").ReadAllRows().Count);
    }

    [Fact]
    public void WideKeysPastTheOldRefusalPoint_StayFindableThroughTheIndex()
    {
        const int Rows = 1200;

        using (var db = CreateAndReopen())
        {
            var table = db.GetTable("T");
            for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("T");

        for (int i = 0; i < Rows; i++)
            Assert.NotNull(reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(KeyFor(i)));
    }

    /// <summary>
    /// The flag no longer suppresses anything, but it is public API and callers set it, so it has
    /// to keep flowing through and leave the index complete either way.
    /// </summary>
    [Fact]
    public void WithForceIgnoreIndexCheck_TheIndexIsStillMaintained()
    {
        const int Rows = 300;

        using (var db = CreateAndReopen())
        {
            var table = db.GetTable("T");
            table.ForceIgnoreIndexCheck = true;
            for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("T");

        Assert.Equal(Rows, reread.ReadAllRows().Count);
        for (int i = 0; i < Rows; i++)
            Assert.NotNull(reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(KeyFor(i)));
    }

    [Fact]
    public void ImportOptions_CarriesTheOverrideOntoTheTable()
    {
        using var db = CreateAndReopen();
        var table = db.ImportTable(
            new[] { new { Key = "a", Id = 1 } },
            tableName: "T",
            options: new ImportOptions { AppendIfExists = true, ForceIgnoreIndexCheck = true });

        Assert.True(table.ForceIgnoreIndexCheck);
    }
}
