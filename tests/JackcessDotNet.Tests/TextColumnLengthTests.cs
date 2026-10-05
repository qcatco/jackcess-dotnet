using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A value longer than its Text column is refused, as Access refuses one (qcatco/exportmdb.interop#20). Written
/// anyway, ACE read it cut to the column's length - a 12-character date in an 11-character column came back
/// "14-Sept-202" - and a typed reader refused the whole table.
/// </summary>
public sealed class TextColumnLengthTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"textlen_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Text_longer_than_its_column_is_refused_rather_than_written()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Dates", new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Date", DataType.Text).MaxLength(11).Build(),
        });
        table.Insert(new Row { ["Id"] = 1, ["Date"] = "14-Sep-2026" });

        var ex = Assert.Throws<ColumnValueTooLongException>(() => table.Insert(new Row { ["Id"] = 2, ["Date"] = "14-Sept-2026" }));

        Assert.Equal("Date", ex.Column);
        Assert.Equal(11, ex.MaxLength);
        Assert.Equal(12, ex.Length);
        Assert.Contains("Date", ex.Message);
        var rows = table.ReadAllRows();
        Assert.Single(rows);
        Assert.Equal("14-Sep-2026", rows[0]["Date"]);
    }

    [Fact]
    public void A_row_is_checked_against_its_columns_without_being_written()
    {
        // What a caller checks a whole batch of rows with before it writes any, so that a refused value stops the
        // batch before the file holds half of it.
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Dates", new[] { new ColumnBuilder("Date", DataType.Text).MaxLength(11).Build() });

        table.Validate(new Row { ["Date"] = "14-Sep-2026" });
        var ex = Assert.Throws<ColumnValueTooLongException>(() => table.Validate(new Row { ["Date"] = "14-Sept-2026" }));

        Assert.Equal("Date", ex.Column);
        Assert.Empty(table.ReadAllRows());
    }

    [Fact]
    public void Text_as_long_as_its_column_is_written_whole()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        var table = db.CreateTable("Names", new[] { new ColumnBuilder("Name", DataType.Text).MaxLength(255).Build() });
        var longest = new string('n', 255);

        table.Insert(new Row { ["Name"] = longest });

        Assert.Equal(longest, table.ReadAllRows()[0]["Name"]);
    }
}
