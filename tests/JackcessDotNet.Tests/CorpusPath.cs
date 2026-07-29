using System.IO;

namespace JackcessDotNet.Tests;

/// <summary>
/// Locates the Jackcess Java test corpus — the ~100 real Access files the
/// corpus-driven theories read. Resolution order:
/// <list type="number">
///   <item>the <c>JACKCESS_CORPUS_PATH</c> environment variable;</item>
///   <item>a <c>jackcess-jackcess-5.0.0</c> checkout sitting beside this repo
///         (walking up from the test binaries), which is how the repo is laid
///         out in practice;</item>
///   <item>a last-resort absolute path.</item>
/// </list>
/// Returns <c>null</c> when no corpus is present, in which case the theories
/// yield zero cases. This lived as three separate copies — two of them
/// absolute-path-only, which silently stopped resolving when the repo moved and
/// left four theories failing as "No data found" — so it is deliberately the one
/// place that knows where the corpus is.
/// </summary>
internal static class CorpusPath
{
    public static string? Resolve()
    {
        string? env = Environment.GetEnvironmentVariable("JACKCESS_CORPUS_PATH");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        string here = AppContext.BaseDirectory;
        for (int up = 0; up < 8; up++)
        {
            string candidate = Path.GetFullPath(Path.Combine(here, "..",
                "jackcess-jackcess-5.0.0", "src", "test", "resources", "data"));
            if (Directory.Exists(candidate)) return candidate;
            here = Path.GetFullPath(Path.Combine(here, ".."));
        }

        const string fallback = @"D:/Projects/byAI/Jackcess/jackcess-jackcess-5.0.0/src/test/resources/data";
        return Directory.Exists(fallback) ? fallback : null;
    }
}
