using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Rows of a table shaped like the flat table behind an attachment column: several fixed columns
/// interleaved with OLE, Memo and Text. Reading one of those from <c>complexDataV2007.accdb</c>
/// returns most columns null — including the one linking back to the owning row — while
/// <c>FileName</c> and <c>FileType</c> decode and the leading Long reads a nonsense value.
/// <c>MSysResources</c>'s flat table does the same, so it is the row decoding rather than anything
/// about attachments.
/// </summary>
public sealed class MixedColumnRowTests : IDisposable
{
    private readonly string _path;
    public MixedColumnRowTests()
        => _path = Path.Combine(Path.GetTempPath(), $"mixedrow_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>The attachment flat table's shape, column for column and in its order.</summary>
    private static IReadOnlyList<Column> AttachmentShape() => new[]
    {
        new ColumnBuilder("FileKey",       DataType.Long).Build(),
        new ColumnBuilder("FileData",      DataType.Ole).Build(),
        new ColumnBuilder("FileFlags",     DataType.Long).Build(),
        new ColumnBuilder("FileName",      DataType.Text).MaxLength(255).Build(),
        new ColumnBuilder("FileTimeStamp", DataType.ShortDateTime).Build(),
        new ColumnBuilder("FileType",      DataType.Text).MaxLength(255).Build(),
        new ColumnBuilder("FileURL",       DataType.Memo).Build(),
        new ColumnBuilder("OwnerFk",       DataType.Long).Build(),
    };

    private static Row SampleRow() => new()
    {
        ["FileKey"]       = 8,
        ["FileData"]      = new byte[] { 1, 2, 3, 4, 5 },
        ["FileFlags"]     = 0,
        ["FileName"]      = "test_data.txt",
        ["FileTimeStamp"] = new DateTime(2024, 3, 4, 5, 6, 7),
        ["FileType"]      = "txt",
        ["FileURL"]       = "http://example.invalid/test_data.txt",
        ["OwnerFk"]       = 3,
    };

    [Fact]
    public void EveryColumnOfTheAttachmentShape_RoundTrips()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Flat", AttachmentShape(), primaryKey: null);
            t.Insert(SampleRow());
        }

        using var db2 = Database.Open(_path);
        Row row = Assert.Single(db2.GetTable("Flat").ReadAllRows());

        var expected = SampleRow();

        // Named one at a time: a blanket loop would report the first failure and hide the shape of
        // the rest, and which columns survive is the whole diagnostic here.
        Assert.Equal(expected["FileKey"],       row["FileKey"]);
        Assert.Equal(expected["FileFlags"],     row["FileFlags"]);
        Assert.Equal(expected["FileName"],      row["FileName"]);
        Assert.Equal(expected["FileTimeStamp"], row["FileTimeStamp"]);
        Assert.Equal(expected["FileType"],      row["FileType"]);
        Assert.Equal(expected["FileURL"],       row["FileURL"]);
        Assert.Equal(expected["OwnerFk"],       row["OwnerFk"]);
        Assert.Equal((byte[])expected["FileData"]!, (byte[])row["FileData"]!);
    }

    /// <summary>
    /// The same shape with the nullable columns actually null, since a null mask read at the wrong
    /// offset is the likeliest way for live values to come back as null.
    /// </summary>
    [Fact]
    public void TheAttachmentShape_DistinguishesRealNullsFromValues()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("Flat", AttachmentShape(), primaryKey: null);
            t.Insert(new Row
            {
                ["FileKey"]  = 8,
                ["FileName"] = "only-a-name.txt",
                ["OwnerFk"]  = 3,
            });
        }

        using var db2 = Database.Open(_path);
        Row row = Assert.Single(db2.GetTable("Flat").ReadAllRows());

        Assert.Equal(8, row["FileKey"]);
        Assert.Equal("only-a-name.txt", row["FileName"]);
        Assert.Equal(3, row["OwnerFk"]);

        Assert.False(row.ContainsKey("FileData"),      "FileData was not written and should read back absent");
        Assert.False(row.ContainsKey("FileURL"),       "FileURL was not written and should read back absent");
        Assert.False(row.ContainsKey("FileTimeStamp"), "FileTimeStamp was not written and should read back absent");
    }
}
