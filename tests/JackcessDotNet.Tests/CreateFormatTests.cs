using System.IO;
using System.Text;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What <see cref="Database.Create"/> will and will not produce.
/// <para>
/// It used to accept every <see cref="JetVersion"/> and write a Jet 4 file for all of them, so
/// <c>Create("x.accdb", JetVersion.Jet16)</c> produced a file whose header said
/// <c>Standard Jet DB</c> version 0x01. That looked like it worked — the ACE engine opens Jet 4
/// whatever the extension — but nothing about the file was ACE format.
/// </para>
/// </summary>
public sealed class CreateFormatTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (string p in _paths)
            if (File.Exists(p)) File.Delete(p);
    }

    private string TempPath(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"createfmt_{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    [Theory]
    [InlineData(JetVersion.Jet12)]
    [InlineData(JetVersion.Jet14)]
    [InlineData(JetVersion.Jet16)]
    [InlineData(JetVersion.Jet17)]
    public void CreatingAnAceDatabase_IsRefusedRatherThanWrittenAsJet4(JetVersion version)
    {
        string path = TempPath(".accdb");

        var ex = Assert.Throws<NotSupportedException>(() => Database.Create(path, version));

        Assert.Contains(version.ToString(), ex.Message);
        Assert.Contains("Jet4", ex.Message);
        Assert.False(File.Exists(path), "a refused create must not leave a file behind");
    }

    [Fact]
    public void CreatingAJet4Database_WritesAJet4Header()
    {
        string path = TempPath(".mdb");

        using (var db = Database.Create(path, JetVersion.Jet4))
            db.CreateTable("T", new[] { new ColumnBuilder("Id", DataType.Long).Build() }, primaryKey: "Id");

        byte[] head = File.ReadAllBytes(path).Take(0x15).ToArray();
        Assert.Equal("Standard Jet DB", Encoding.ASCII.GetString(head, 4, 15));
        Assert.Equal(0x01, head[0x14]);
    }

    /// <summary>Reading ACE files is unaffected — that half has always worked.</summary>
    [Fact]
    public void AnExistingAceDatabaseCanStillBeOpened()
    {
        string? corpus = CorpusPath.Resolve();
        if (corpus is null) return;

        string source = Path.Combine(corpus, "V2007", "blobV2007.accdb");
        if (!File.Exists(source)) return;

        using var db = Database.Open(source);
        Assert.NotEmpty(db.ListTables());
    }
}
