using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Reclaiming the space a deleted row leaves behind on a page that still holds live rows.
/// <para>
/// Deleting only flags the slot; the bytes stay where they are and the page's free-space figure
/// does not move, so that space was unusable. Closing the gaps means moving row data — and an index
/// entry points at <c>(page &lt;&lt; 16) | slot</c>, so the slot a row sits in has to survive the
/// move or every index in the table would be left pointing at the wrong rows.
/// </para>
/// </summary>
public sealed class PageCompactionTests : IDisposable
{
    private readonly string _path;
    public PageCompactionTests()
        => _path = Path.Combine(Path.GetTempPath(), $"compact_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Body", DataType.Text).MaxLength(200).Build(),
    };

    /// <summary>Rows fat enough that a 4 KB page holds roughly twenty.</summary>
    private static Row RowFor(int i) => new() { ["Id"] = i, ["Body"] = $"{i:D4}-" + new string('b', 180) };

    private void Seed(int rows)
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var t = db.CreateTable("T", Columns(), primaryKey: "Id");
        for (int i = 0; i < rows; i++) t.Insert(RowFor(i));
    }

    [Fact]
    public void SpaceFromDeletedRows_IsReusedByLaterInserts()
    {
        const int Rows = 200;
        Seed(Rows);

        long afterSeed = new FileInfo(_path).Length;

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            // Delete every other row, so no page empties completely — the all-deleted shortcut
            // must not be what reclaims this.
            for (int i = 0; i < Rows; i += 2) t.DeleteRow("Id", i);
            for (int i = Rows; i < Rows + Rows / 2; i++) t.Insert(RowFor(i));
        }

        long afterChurn = new FileInfo(_path).Length;

        Assert.True(afterChurn <= afterSeed,
            $"file grew from {afterSeed} to {afterChurn} bytes after deleting half the rows and "
          + "writing as many again, so the freed space is not being reused");
    }

    /// <summary>
    /// The point of the exercise: rows have moved within their page, and every index must still
    /// find them. A compaction that renumbered slots would fail here and nowhere else.
    /// </summary>
    [Fact]
    public void AfterSpaceIsReclaimed_EveryRemainingRowIsStillFoundThroughTheIndex()
    {
        const int Rows = 200;
        Seed(Rows);

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            for (int i = 0; i < Rows; i += 2) t.DeleteRow("Id", i);
            for (int i = Rows; i < Rows + Rows / 2; i++) t.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var table = reopened.GetTable("T");

        // Survivors from the first batch.
        for (int i = 1; i < Rows; i += 2)
        {
            Row? found = table.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i);
            Assert.NotNull(found);
            Assert.Equal(RowFor(i)["Body"], found!["Body"]);
        }

        // And the rows written afterwards.
        for (int i = Rows; i < Rows + Rows / 2; i++)
            Assert.NotNull(table.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i));

        // Deleted ones stay gone.
        for (int i = 0; i < Rows; i += 2)
            Assert.Null(table.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(i));
    }

    [Fact]
    public void AfterSpaceIsReclaimed_TheRowsThemselvesAreStillIntact()
    {
        const int Rows = 120;
        Seed(Rows);

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            for (int i = 0; i < Rows; i += 3) t.DeleteRow("Id", i);
        }

        using var reopened = Database.Open(_path);
        var rows = reopened.GetTable("T").ReadAllRows();

        var expected = Enumerable.Range(0, Rows).Where(i => i % 3 != 0).ToList();
        Assert.Equal(expected.Count, rows.Count);

        foreach (var row in rows)
        {
            int id = (int)row["Id"]!;
            Assert.Equal(RowFor(id)["Body"], row["Body"]);
        }
    }

    /// <summary>Deleting everything on a page still hands the whole page back.</summary>
    [Fact]
    public void APageEmptiedCompletely_IsStillReclaimedWholesale()
    {
        const int Rows = 60;
        Seed(Rows);

        long afterSeed = new FileInfo(_path).Length;

        using (var db = Database.Open(_path))
        {
            var t = db.GetTable("T");
            for (int i = 0; i < Rows; i++) t.DeleteRow("Id", i);
            for (int i = Rows; i < Rows * 2; i++) t.Insert(RowFor(i));
        }

        Assert.True(new FileInfo(_path).Length <= afterSeed * 3 / 2);
    }
}
