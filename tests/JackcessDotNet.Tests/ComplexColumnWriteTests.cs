using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Table.AddComplexValue"/> and the state of writing to ACE-format files.
/// <para>
/// The write path is built and its link handling is derived from the on-disk evidence: the flat
/// row's foreign key is the owning row's complex id, and its own id counts up across the whole flat
/// table. It cannot be exercised end to end, because complex columns exist only in <c>.accdb</c>
/// files and <b>writing to an .accdb does not work at all</b> — a plain insert into an ordinary
/// table of one throws while reading a page number far past the end of the file. Every write test
/// in this suite runs against Jet 4 <c>.mdb</c>, which is how that went unnoticed.
/// </para>
/// <para>
/// The test below pins that blocker. It is expected to start failing the day ACE writing works,
/// which is the point: that is when the complex-column write path can be verified and this file
/// replaced with tests that actually exercise it.
/// </para>
/// </summary>
public sealed class ComplexColumnWriteTests : IDisposable
{
    private readonly string _path;
    public ComplexColumnWriteTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cplxwrite_{Guid.NewGuid():N}.accdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private bool TryStage()
    {
        string? corpus = CorpusPath.Resolve();
        if (corpus is null) return false;
        string source = Path.Combine(corpus, "V2007", "complexDataV2007.accdb");
        if (!File.Exists(source)) return false;
        File.Copy(source, _path, overwrite: true);
        return true;
    }

    /// <summary>
    /// Writing to an ACE-format file is broken, independently of anything to do with complex
    /// columns. Recorded as a test so the limitation is visible in the suite rather than only in a
    /// document, and so it announces itself when fixed.
    /// </summary>
    [Fact]
    public void WritingToAnAccdb_DoesNotWorkYet()
    {
        if (!TryStage()) return;

        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");

        Assert.ThrowsAny<Exception>(
            () => table.Insert(new Row { ["id"] = "probe-row", ["memo-data"] = "hello" }));
    }

    /// <summary>The argument checks do not depend on the write ever reaching the file.</summary>
    [Fact]
    public void AddingToSomethingThatIsNotAComplexColumn_SaysSo()
    {
        if (!TryStage()) return;

        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");
        Row row = table.ReadAllRows().First();

        var ex = Assert.Throws<InvalidOperationException>(
            () => table.AddComplexValue(row, "memo-data", new Row { ["Value"] = "x" }));
        Assert.Contains("not a complex column", ex.Message);
    }

    [Fact]
    public void AddingToARowWithNoComplexId_SaysSo()
    {
        if (!TryStage()) return;

        using var db = Database.Open(_path);
        var table = db.GetTable("Table1");

        var ex = Assert.Throws<InvalidOperationException>(
            () => table.AddComplexValue(new Row { ["id"] = "detached" }, "multi-value-data",
                                        new Row { ["Value"] = "x" }));
        Assert.Contains("no complex id", ex.Message);
    }
}
