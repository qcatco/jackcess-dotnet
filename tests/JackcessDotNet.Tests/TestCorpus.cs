using System.IO;

namespace JackcessDotNet.Tests;

/// <summary>
/// Single source of truth for where the Access test corpus lives.
///
/// Five test classes used to carry their own copy of this logic, four of them
/// hardcoded to <c>D:/Projects/jackcess-jackcess-5.0.0/...</c> — a path that
/// exists on exactly one machine. Anywhere else the corpus theories yielded no
/// cases, and xUnit treats a <c>[Theory]</c> with no data as a FAILURE, not a
/// skip. That is why CI had been red since the workflow was added.
///
/// The corpus is now committed under <c>tests/corpus</c> (see its README), so
/// <see cref="Root"/> resolves on every machine and the theories always have
/// data. <c>JACKCESS_CORPUS_PATH</c> still overrides, for pointing the suite at
/// a larger or private set of files.
/// </summary>
public static class TestCorpus
{
    /// <summary>The corpus directory, or null if it genuinely cannot be found.</summary>
    public static string? Root { get; } = Resolve();

    /// <summary>Absolute path to a named corpus file, e.g. Path("V2003", "common1V2003.mdb").</summary>
    public static string? File(string version, string filename)
    {
        if (Root is null) return null;
        string path = System.IO.Path.Combine(Root, version, filename);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Every file matching <paramref name="pattern"/> in the given version folders.</summary>
    public static IEnumerable<string> Files(string pattern, params string[] versions)
    {
        if (Root is null) yield break;
        foreach (string version in versions)
        {
            string dir = System.IO.Path.Combine(Root, version);
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, pattern))
                yield return file;
        }
    }

    private static string? Resolve()
    {
        string? env = Environment.GetEnvironmentVariable("JACKCESS_CORPUS_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        // Walk up from the test binaries to the repo root and look for tests/corpus.
        // bin/<cfg>/<tfm> is three levels under the test project, which is two
        // under the repo root — six is comfortable headroom.
        string dir = AppContext.BaseDirectory;
        for (int up = 0; up < 8 && dir.Length > 0; up++)
        {
            string candidate = System.IO.Path.Combine(dir, "tests", "corpus");
            if (Directory.Exists(System.IO.Path.Combine(candidate, "V2003"))) return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }

        // Legacy locations: a sibling checkout of the Java project, then the
        // original author's absolute path. Kept so nobody's existing setup breaks.
        string sibling = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
            "jackcess-jackcess-5.0.0", "src", "test", "resources", "data"));
        if (Directory.Exists(sibling)) return sibling;

        const string hardcoded = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(hardcoded) ? hardcoded : null;
    }
}
