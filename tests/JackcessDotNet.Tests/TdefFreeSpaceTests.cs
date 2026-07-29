using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Bytes 2-3 of a table-definition page hold the page's remaining free space. Access accounts for
/// it as <c>PageSize - 8 - definitionLength</c>. The writer computed exactly that and then had it
/// overwritten by the zeroed prefix bytes written immediately after, so every definition this
/// library produced recorded 0 while the code read as though it recorded the real figure.
/// </summary>
public sealed class TdefFreeSpaceTests : IDisposable
{
    private readonly string _path;
    public TdefFreeSpaceTests()
        => _path = Path.Combine(Path.GetTempPath(), $"tdeffree_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Id",   DataType.Long).Build(),
        new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
    };

    /// <summary>Reads (free space, definition length) straight off a table's TDEF page.</summary>
    private (int FreeSpace, int ContentSize) ReadTdefHeader(bool withPrimaryKey)
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", Columns(), primaryKey: withPrimaryKey ? "Id" : null);

        using var db2 = Database.Open(_path);
        int tdefPage = db2.GetTable("T").Definition.TdefPageNumber;

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] page = pf.ReadPage(tdefPage);

        Assert.Equal(JetFormat.PageTypeTableDef, page[0]);
        return (ByteUtil.GetUShort(page, 2), ByteUtil.GetInt(page, 8));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATableDefinition_RecordsItsRemainingFreeSpace(bool withPrimaryKey)
    {
        var (freeSpace, contentSize) = ReadTdefHeader(withPrimaryKey);

        Assert.True(contentSize > 0, "the definition should have a length");
        Assert.Equal(JetFormat.Jet4.PageSize - 8 - contentSize, freeSpace);
        Assert.NotEqual(0, freeSpace);
    }

    /// <summary>
    /// The prefix bytes the free-space field shares its page with still have to be right — that is
    /// what broke it, and the fix moves one write after the other.
    /// </summary>
    [Fact]
    public void ThePagePrefixIsStillIntact()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", Columns(), primaryKey: "Id");

        using var db2 = Database.Open(_path);
        int tdefPage = db2.GetTable("T").Definition.TdefPageNumber;

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] page = pf.ReadPage(tdefPage);

        Assert.Equal(JetFormat.PageTypeTableDef, page[0]);
        Assert.Equal(0x01, page[1]);
        Assert.Equal(0, ByteUtil.GetInt(page, 4));   // no continuation page
    }

    /// <summary>
    /// Adding an index rewrites the definition and has to take its own bytes off the figure, now
    /// that there is a real figure to take them off.
    /// </summary>
    [Fact]
    public void AddingAnIndex_LeavesTheFreeSpaceConsistentWithTheNewLength()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", Columns(), primaryKey: "Id");

        using (var db = Database.Open(_path))
            db.CreateIndex("T", "ByName", "Name");

        using var db2 = Database.Open(_path);
        int tdefPage = db2.GetTable("T").Definition.TdefPageNumber;

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] page = pf.ReadPage(tdefPage);

        int freeSpace   = ByteUtil.GetUShort(page, 2);
        int contentSize = ByteUtil.GetInt(page, 8);

        Assert.Equal(JetFormat.Jet4.PageSize - 8 - contentSize, freeSpace);
    }
}
