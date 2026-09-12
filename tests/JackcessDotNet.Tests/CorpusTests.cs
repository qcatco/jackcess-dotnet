using System.IO;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace JackcessDotNet.Tests;

/// <summary>
/// Reality-check tests that open .mdb files from the Jackcess Java test corpus
/// (V1997 / V2000 / V2003 — V2007+ .accdb files are skipped: codec not yet implemented).
///
/// The corpus is committed under tests/corpus and located by <see cref="TestCorpus"/>,
/// so these theories always have data. (An empty [Theory] is an xUnit FAILURE, not a
/// skip — when the corpus was resolved from an absolute path on one machine, that
/// alone made the run red everywhere else.)
/// Each file is its own theory invocation so the failure list maps 1:1 to broken files.
/// </summary>
public sealed class CorpusTests
{
    private readonly ITestOutputHelper _output;

    public CorpusTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> CorpusFiles()
    {
        foreach (string file in TestCorpus.Files("*.mdb", "V1997", "V2000", "V2003"))
            yield return new object[] { VersionOf(file), Path.GetFileName(file), file };

        foreach (string file in TestCorpus.Files("*.accdb", "V2007", "V2010", "V2019"))
            yield return new object[] { VersionOf(file), Path.GetFileName(file), file };
    }

    private static string VersionOf(string file)
        => Path.GetFileName(Path.GetDirectoryName(file)!);

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Open_ListTables_ReadFirstRows(string version, string filename, string path)
    {
        var report = new StringBuilder();
        report.AppendLine($"=== {version}/{filename} ===");

        Database? db = null;
        try
        {
            db = Database.Open(path);
            report.AppendLine("  open: OK");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  open: FAIL — {ex.GetType().Name}: {ex.Message}");
            _output.WriteLine(report.ToString());
            Assert.Fail(report.ToString());
            return;
        }

        var failures = new List<string>();
        try
        {
            var tableNames = db.ListTables(includeSystem: false);
            report.AppendLine($"  user tables ({tableNames.Count}): {string.Join(", ", tableNames)}");

            var sysTables = db.ListTables(includeSystem: true);
            report.AppendLine($"  total tables (incl. system): {sysTables.Count}");

            foreach (string name in tableNames)
            {
                try
                {
                    var table = db.GetTable(name);
                    var rows  = table.ReadAllRows();
                    int sample = Math.Min(rows.Count, 10);
                    report.AppendLine($"  table '{name}': {table.Columns.Count} cols, {rows.Count} rows read (sample {sample})");
                }
                catch (Exception ex)
                {
                    string line = $"  table '{name}': FAIL — {ex.GetType().Name}: {ex.Message}";
                    report.AppendLine(line);
                    failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            db.Dispose();
        }

        _output.WriteLine(report.ToString());
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} table(s) failed in {version}/{filename}:\n  - "
                        + string.Join("\n  - ", failures));
    }
}
