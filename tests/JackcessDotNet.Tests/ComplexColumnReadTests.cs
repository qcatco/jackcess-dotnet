using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Read support for complex columns (Access 2007+ multi-value fields, attachment fields and
/// append-only memos), checked against the upstream corpus file that actually contains them.
/// Skips when the corpus isn't present.
/// <para>
/// A complex column stores a 4-byte id in the row; the values live in a per-column flat table
/// that <c>MSysComplexColumns</c> points to. Before this the column was dropped from every row
/// entirely, so nothing could be resolved.
/// </para>
/// </summary>
public sealed class ComplexColumnReadTests
{
    private static string? ComplexFile()
    {
        string? corpus = CorpusPath.Resolve();
        if (corpus is null) return null;
        string p = Path.Combine(corpus, "V2007", "complexDataV2007.accdb");
        return File.Exists(p) ? p : null;
    }

    [Fact]
    public void ComplexColumns_SurfaceTheirIdOnTheRow()
    {
        string? file = ComplexFile();
        if (file is null) return;

        using var db = Database.Open(file);
        var table = db.GetTable("Table1");

        var complexCols = table.Columns.Where(c => c.DataType == DataType.Complex).ToList();
        Assert.NotEmpty(complexCols);

        foreach (Row row in table.ReadAllRows())
            foreach (var col in complexCols)
            {
                Assert.True(row.ContainsKey(col.Name), $"'{col.Name}' missing from the row");
                Assert.IsType<int>(row[col.Name]);
            }
    }

    [Fact]
    public void GetComplexValues_ReturnsAMultiValueFieldsValues()
    {
        string? file = ComplexFile();
        if (file is null) return;

        using var db = Database.Open(file);
        var table = db.GetTable("Table1");
        var rows  = table.ReadAllRows();

        // row3 carries four values in the multi-value field; row1 carries none.
        var row3 = rows.Single(r => (string?)r["id"] == "row3");
        var values = table.GetComplexValues(row3, "multi-value-data")
                          .Select(v => (string?)v["Value"])
                          .ToList();

        Assert.Equal(new[] { "value1", "value2", "value3", "value4" }, values);

        var row1 = rows.Single(r => (string?)r["id"] == "row1");
        Assert.Empty(table.GetComplexValues(row1, "multi-value-data"));
    }

    [Fact]
    public void GetComplexValues_ReturnsVersionHistoryEntries()
    {
        string? file = ComplexFile();
        if (file is null) return;

        using var db = Database.Open(file);
        var table = db.GetTable("Table1");
        var rows  = table.ReadAllRows();

        string vhColumn = table.Columns
            .Single(c => c.DataType == DataType.Complex
                      && c.Name.StartsWith("VersionHistory", StringComparison.Ordinal))
            .Name;

        // row3 has been edited three times; row1 has no history.
        Assert.Equal(3, table.GetComplexValues(rows.Single(r => (string?)r["id"] == "row3"), vhColumn).Count);
        Assert.Empty(table.GetComplexValues(rows.Single(r => (string?)r["id"] == "row1"), vhColumn));
    }

    [Fact]
    public void GetComplexValues_OnANonComplexColumn_IsEmptyRatherThanThrowing()
    {
        string? file = ComplexFile();
        if (file is null) return;

        using var db = Database.Open(file);
        var table = db.GetTable("Table1");
        var row   = table.ReadAllRows().First();

        Assert.Empty(table.GetComplexValues(row, "id"));
        Assert.Empty(table.GetComplexValues(row, "no-such-column"));
    }
}
