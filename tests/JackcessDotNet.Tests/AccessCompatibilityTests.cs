using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Locks down the on-disk details that decide whether Microsoft Access can read a table this
/// library created. Each one was a real defect: the library round-tripped its own files
/// perfectly while Access either could not see the table or reported "Not a valid bookmark",
/// so none of these is covered by any behavioural test — only by the exact bytes.
/// <para>
/// The end-to-end check is "open it with Microsoft.ACE.OLEDB.12.0", which is not portable, so
/// these assert the byte-level invariants that check bought instead.
/// </para>
/// </summary>
public sealed class AccessCompatibilityTests : IDisposable
{
    private readonly string _path;
    public AccessCompatibilityTests()
        => _path = Path.Combine(Path.GetTempPath(), $"acc_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>Top-level parent of the container objects (Jackcess DB_PARENT_ID).</summary>
    private const int DatabaseParentId = 0x0F000000;

    private static IReadOnlyList<Column> MixedColumns() => new[]
    {
        new ColumnBuilder("N0",  DataType.Long).Build(),                 // fixed
        new ColumnBuilder("T1",  DataType.Text).MaxLength(50).Build(),   // var  → takes index 0
        new ColumnBuilder("N2",  DataType.Long).Build(),                 // fixed
        new ColumnBuilder("N3",  DataType.Money).Build(),                // fixed
        new ColumnBuilder("T4",  DataType.Text).MaxLength(50).Build(),   // var  → takes index 1
        new ColumnBuilder("N5",  DataType.Long).Build(),                 // fixed
    };

    // ── The usage-map page is a DATA page ────────────────────────────────────

    [Fact]
    public void CreateUmapPage_IsADataPage_NotAUsageMapPage()
    {
        // Access keeps a table's owned/free maps as rows of a data page. Type 0x05 is only for
        // the global usage map and the bitmap pages a reference map points at; writing 0x05
        // here left Access unable to resolve any row ("Not a valid bookmark").
        byte[] page = UsageMap.CreateUmapPage(JetFormat.Jet4);

        Assert.Equal(JetFormat.PageTypeData, page[0]);
        Assert.NotEqual(JetFormat.PageTypeUsageMap, page[0]);
        Assert.Equal(2, ByteUtil.GetShort(page, JetFormat.Jet4.OffsetDataNumRows));   // owned + free
    }

    [Fact]
    public void CreatedTable_UsageMapPage_IsADataPage()
    {
        var format = JetFormat.Jet4;
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", MixedColumns());

        byte[] file = File.ReadAllBytes(_path);
        int umapPage = -1;
        for (int p = 0; (long)p * format.PageSize < file.Length && umapPage < 0; p++)
        {
            int b = p * format.PageSize;
            if (file[b] != 0x02) continue;
            var tdef = new byte[format.PageSize];
            Array.Copy(file, b, tdef, 0, format.PageSize);
            try
            {
                var info = TdefReader.Read(tdef, format);
                if (info.Columns.Any(c => c.Name == "T4")) umapPage = info.OwnedPagesUmapPage;
            }
            catch { }
        }

        Assert.True(umapPage > 0, "table's usage-map page not found");
        Assert.Equal(JetFormat.PageTypeData, file[umapPage * format.PageSize]);
    }

    // ── Column-header fields Access relies on ────────────────────────────────

    [Fact]
    public void Serialize_EveryColumnCarriesTheRunningVarTableIndex()
    {
        var format = JetFormat.Jet4;
        var def = new TableDefinition("T", MixedColumns());
        byte[] page = def.Serialize(format);

        // A fixed column stores the index the *next* variable column will take, so the
        // sequence continues across fixed columns instead of resetting to 0.
        int[] expected = { 0, 0, 1, 1, 1, 2 };
        for (int i = 0; i < expected.Length; i++)
        {
            int p = format.SizeTdefHeader + i * format.SizeColumnHeader;
            Assert.Equal(expected[i], ByteUtil.GetShort(page, p + format.OffsetColumnVarTableIndex));
        }
    }

    [Fact]
    public void Serialize_StampsTheTextSortOrderOnEveryNonNumericColumn()
    {
        var format = JetFormat.Jet4;
        var def = new TableDefinition("T", MixedColumns());
        byte[] page = def.Serialize(format);

        // 0x0409 = LCID 1033, general-legacy. Access writes it on Long and Money too, not
        // just on text columns.
        for (int i = 0; i < MixedColumns().Count; i++)
        {
            int p = format.SizeTdefHeader + i * format.SizeColumnHeader;
            Assert.Equal(1033, ByteUtil.GetShort(page, p + format.OffsetColumnPrecision));
        }
    }

    [Fact]
    public void Serialize_KeepsPrecisionAndScaleForNumericColumns()
    {
        var format = JetFormat.Jet4;
        var def = new TableDefinition("T", new[]
        {
            new ColumnBuilder("D", DataType.Numeric).WithNumericScale(12, 3).Build(),
        });
        byte[] page = def.Serialize(format);

        int p = format.SizeTdefHeader;
        Assert.Equal(12, page[p + format.OffsetColumnPrecision]);
        Assert.Equal(3,  page[p + format.OffsetColumnScale]);
    }

    // ── Index entries have to be in Jet's format, not just self-consistent ───

    [Fact]
    public void EncodeCompositeKeyBytes_FramesEveryColumnWithItsOwnStartFlag()
    {
        byte[] key = IndexWriter.EncodeCompositeKeyBytes(new object?[] { 1, "AB" });

        // [0x7F][4-byte int, sign-bit flipped][0x7F][collated text…] — one flag per column.
        Assert.Equal(0x7F, key[0]);
        Assert.Equal(0x80, key[1]);          // 1 with the sign bit flipped → 0x80 00 00 01
        Assert.Equal(0x00, key[2]);
        Assert.Equal(0x00, key[3]);
        Assert.Equal(0x01, key[4]);
        Assert.Equal(0x7F, key[5]);          // ← the per-column flag that used to be missing
        Assert.True(key.Length > 6, "text segment missing");
    }

    [Fact]
    public void EncodeCompositeKeyBytes_NullColumnCollapsesToTheNullFlag()
    {
        byte[] key = IndexWriter.EncodeCompositeKeyBytes(new object?[] { null, 1 });

        Assert.Equal(0x00, key[0]);          // null flag, no value bytes
        Assert.Equal(0x7F, key[1]);          // next column's own flag
    }

    // ── The catalog row Access enumerates ────────────────────────────────────

    [Fact]
    public void CreatedTable_IsParentedToTheTablesContainer_WithAnOwner()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.CreateTable("Wares", MixedColumns());

        var catalog = db.GetTable("MSysObjects").ReadAllRows();

        var container = catalog.SingleOrDefault(r =>
            (r["Name"] as string) == "Tables"
            && r["ParentId"] is int cp && cp == DatabaseParentId);
        Assert.NotNull(container);

        var row = catalog.SingleOrDefault(r => (r["Name"] as string) == "Wares");
        Assert.NotNull(row);
        Assert.Equal((int)container!["Id"]!, (int)row!["ParentId"]!);
        Assert.NotEqual(0, (int)row["ParentId"]!);
        Assert.NotNull(row["Owner"] as byte[]);
    }

    [Fact]
    public void CreatedTable_IsFindableThroughTheParentIdNameIndex()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.CreateTable("Wares", MixedColumns());

        var msys = db.GetTable("MSysObjects");
        int parentId = msys.ReadAllRows()
            .Single(r => (r["Name"] as string) == "Wares")["ParentId"] is int p ? p : 0;

        // This is how Access enumerates objects. Before the composite-key fix the entry we
        // wrote could not be found here — nor by Access — even though the row existed.
        var hit = msys.NewIndexCursor("ParentIdName").FindRowByEntry(parentId, "Wares");

        Assert.NotNull(hit);
        Assert.Equal("Wares", hit!["Name"]);
    }

    [Fact]
    public void CreatedTable_GetsAccessControlEntries()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.CreateTable("Wares", MixedColumns());

        int objectId = (int)db.GetTable("MSysObjects").ReadAllRows()
            .Single(r => (r["Name"] as string) == "Wares")["Id"]!;

        var aces = db.GetTable("MSysACEs").ReadAllRows()
                     .Where(r => r["ObjectId"] is int oid && oid == objectId)
                     .ToList();

        Assert.NotEmpty(aces);
        Assert.All(aces, r => Assert.NotNull(r["SID"] as byte[]));
    }
}
