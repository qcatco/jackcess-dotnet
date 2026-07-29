using System.IO;
using System.Text;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Jet stores Text/Memo either as plain UTF-16LE or "compressed" — a 0xFF 0xFE header
/// followed by one byte per char — and a column may only be compressed when its ext-flags
/// byte says so (<c>COMPRESSED_UNICODE_EXT_FLAG_MASK</c> 0x01, per Jackcess Java's
/// <c>ColumnImpl</c>). Writing compressed text into a column that isn't flagged makes
/// Access read the value as UTF-16: 'ASKARISHAHI' comes back as '十䅋䥒䡓䡁', because the
/// bytes 0x41 0x53 ("AS") pair into U+5341. Only Latin-1-only values are affected, so
/// Persian text hid the bug for a long time.
/// </summary>
public sealed class TextCompressionTests : IDisposable
{
    private readonly string _path;
    public TextCompressionTests()
        => _path = Path.Combine(Path.GetTempPath(), $"txt_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private const string Ascii = "ASKARISHAHI";

    // ── The column's flag decides, not the value ─────────────────────────────

    [Fact]
    public void EncodeText_WhenTheColumnIsNotFlagged_WritesPlainUtf16()
    {
        byte[] encoded = ByteUtil.EncodeText(Ascii, allowCompression: false);

        Assert.Equal(Ascii.Length * 2, encoded.Length);
        Assert.NotEqual(0xFF, encoded[0]);                       // no compression header
        Assert.Equal(Encoding.Unicode.GetBytes(Ascii), encoded);
        Assert.Equal(Ascii, ByteUtil.DecodeText(encoded, 0, encoded.Length));
    }

    [Fact]
    public void EncodeText_WhenTheColumnIsFlagged_WritesTheHeaderAndSingleBytes()
    {
        byte[] encoded = ByteUtil.EncodeText(Ascii, allowCompression: true);

        Assert.Equal(Ascii.Length + 2, encoded.Length);
        Assert.Equal(0xFF, encoded[0]);
        Assert.Equal(0xFE, encoded[1]);
        Assert.Equal(Ascii, ByteUtil.DecodeText(encoded, 0, encoded.Length));
    }

    [Fact]
    public void EncodeText_DefaultsToUncompressed()
    {
        // The safe default: a caller that doesn't know the column's flag must not compress.
        Assert.Equal(Encoding.Unicode.GetBytes(Ascii), ByteUtil.EncodeText(Ascii));
    }

    [Theory]
    [InlineData("")]      // nothing to compress
    [InlineData("A")]     // 1 char: the 2-byte header would make it bigger
    [InlineData("AS")]    // 2 chars: same size at best
    public void EncodeText_TooShortToBeWorthCompressing_StaysUtf16(string value)
    {
        byte[] encoded = ByteUtil.EncodeText(value, allowCompression: true);

        Assert.Equal(Encoding.Unicode.GetBytes(value), encoded);
        Assert.Equal(value, ByteUtil.DecodeText(encoded, 0, encoded.Length));
    }

    [Fact]
    public void EncodeText_WithANulChar_IsNotCompressed()
    {
        // Jet's compressed form switches modes on a 0x00 byte, so a NUL is not
        // compressible (Java: MIN_COMPRESS_CHAR = 1).
        string withNul = "AB\0CD";
        byte[] encoded = ByteUtil.EncodeText(withNul, allowCompression: true);

        Assert.Equal(Encoding.Unicode.GetBytes(withNul), encoded);
        Assert.Equal(withNul, ByteUtil.DecodeText(encoded, 0, encoded.Length));
    }

    [Fact]
    public void EncodeText_NonLatin1_IsNeverCompressed_EvenWhenFlagged()
    {
        const string persian = "تخفیف";
        byte[] encoded = ByteUtil.EncodeText(persian, allowCompression: true);

        Assert.Equal(Encoding.Unicode.GetBytes(persian), encoded);
        Assert.Equal(persian, ByteUtil.DecodeText(encoded, 0, encoded.Length));
    }

    // ── The flag has to survive a round-trip through the TDEF ────────────────

    [Fact]
    public void Serialize_PutsTheCompressedUnicodeBitInTheExtFlagsByte()
    {
        var format = JetFormat.Jet4;
        var def = new TableDefinition("T", new[]
        {
            new ColumnBuilder("Plain",      DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Compressed", DataType.Text).MaxLength(50).CompressedUnicode().Build(),
        });

        byte[] page = def.Serialize(format);

        // No indexes, so the column headers start straight after the TDEF header.
        int colDefStart = format.SizeTdefHeader;
        int plain      = colDefStart;
        int compressed = colDefStart + format.SizeColumnHeader;

        Assert.Equal(0x00, page[plain      + format.OffsetColumnFlags + 1]);
        Assert.Equal(0x01, page[compressed + format.OffsetColumnFlags + 1]);
    }

    [Fact]
    public void CreatedFile_HasTheBitOnDisk()
    {
        var format = JetFormat.Jet4;
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", new[]
            {
                new ColumnBuilder("Plain",      DataType.Text).MaxLength(50).Build(),
                new ColumnBuilder("Compressed", DataType.Text).MaxLength(50).CompressedUnicode().Build(),
            });

        byte[] file = File.ReadAllBytes(_path);
        var found = new List<string>();
        for (int pageNum = 0; pageNum * format.PageSize < file.Length; pageNum++)
        {
            int b = pageNum * format.PageSize;
            if (file[b] != 0x02) continue;                       // not a TDEF page
            int numIndexes  = ByteUtil.GetInt(file, b + format.TdefOffsetNumIndexes);
            int numCols     = ByteUtil.GetShort(file, b + format.TdefOffsetNumCols);
            int colDefStart = b + format.SizeTdefHeader + numIndexes * format.SizeIndexDefinition;
            for (int i = 0; i < numCols && i < 4; i++)
            {
                int p = colDefStart + i * format.SizeColumnHeader;
                found.Add($"page{pageNum} col{i} type=0x{file[p]:X2} "
                        + $"flags=0x{file[p + format.OffsetColumnFlags]:X2} "
                        + $"ext=0x{file[p + format.OffsetColumnFlags + 1]:X2}");
            }
        }
        Assert.Contains(found, s => s.EndsWith("ext=0x01"));

        // Same page, parsed by the reader: the flag must come back out.
        int tdefPage = int.Parse(found.First(s => s.EndsWith("ext=0x01"))
                                      .Split(' ')[0].Substring("page".Length));
        var page = new byte[format.PageSize];
        Array.Copy(file, tdefPage * format.PageSize, page, 0, format.PageSize);
        var info = TdefReader.Read(page, format);

        Assert.True(info.Columns.Any(c => c.IsCompressedUnicode),
            "TdefReader lost the flag: "
            + string.Join(", ", info.Columns.Select(c => $"{c.Name}:{c.DataType}:{c.IsCompressedUnicode}")));
    }

    [Fact]
    public void Column_CompressedUnicode_RoundTripsThroughTheTdef()
    {
        var columns = new[]
        {
            new ColumnBuilder("Plain",      DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Compressed", DataType.Text).MaxLength(50).CompressedUnicode().Build(),
        };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", columns);

        using (var db = Database.Open(_path))
        {
            var read = db.GetTable("T").Columns;
            Assert.False(read.Single(c => c.Name == "Plain").IsCompressedUnicode);
            Assert.True(read.Single(c => c.Name == "Compressed").IsCompressedUnicode);
        }
    }

    [Fact]
    public void Insert_IntoAnUnflaggedTextColumn_StoresUncompressedBytes()
    {
        var columns = new[] { new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build() };

        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var table = db.CreateTable("T", columns);
            table.Insert(new Row { ["Name"] = Ascii });
        }

        // Whatever we wrote must survive our own reader…
        using (var db = Database.Open(_path))
            Assert.Equal(Ascii, db.GetTable("T").ReadAllRows().Single()["Name"]);

        // …and the stored bytes must be plain UTF-16, which is what Access will assume
        // for a column whose ext-flags byte has no 0x01. Byte-scan the file for the
        // UTF-16 form and make sure the compressed form is absent.
        byte[] raw = File.ReadAllBytes(_path);
        Assert.True(Contains(raw, Encoding.Unicode.GetBytes(Ascii)), "UTF-16 form not found");
        var compressed = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Latin1.GetBytes(Ascii)).ToArray();
        Assert.False(Contains(raw, compressed), "compressed form should not be present");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    // ── Real Access files: the flag must be read, not assumed ────────────────

    [Fact]
    public void TdefReader_ReadsTheFlagFromRealAccessFiles()
    {
        string? corpus = CorpusPath.Resolve();
        if (corpus is null) return;   // no corpus on this machine

        int textColumns = 0, flagged = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(corpus, "V2000"), "*.mdb"))
        {
            Database db;
            try { db = Database.Open(file); } catch { continue; }   // encrypted/odd fixtures
            using (db)
            {
                foreach (string name in db.ListTables(includeSystem: false))
                {
                    IReadOnlyList<Column> cols;
                    try { cols = db.GetTable(name).Columns; } catch { continue; }
                    foreach (var c in cols.Where(c => c.DataType == DataType.Text))
                    {
                        textColumns++;
                        if (c.IsCompressedUnicode) flagged++;
                    }
                }
            }
        }

        // Access flags Text fields compressed by default, so a corpus of real files must
        // contain some. If this ever reads zero, the ext-flags offset is wrong.
        Assert.True(textColumns > 0, "no text columns found in the corpus");
        Assert.True(flagged > 0, $"no compressed-unicode text column among {textColumns} — ext-flags offset suspect");
    }
}
