using System.IO;
using System.Text;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Regression tests for four format-correctness defects that prevented files
/// written by this library from being read by Microsoft Access / the ACE OLE DB
/// provider, and prevented Access-written long values from being read here.
///
/// Each test fails against the code as it was before the corresponding fix.
/// They are written to need no external corpus and no Access/ACE installation,
/// so they run anywhere the rest of the suite runs.
/// </summary>
public sealed class AccessInteropRegressionTests : IDisposable
{
    private readonly string _path;
    public AccessInteropRegressionTests()
        => _path = Path.Combine(Path.GetTempPath(), $"interop_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // ── 1. Jet4 compressed-text decoding ─────────────────────────────────────
    // The 0xFF 0xFE marker starts a COMPRESSED run (one byte per char) and every
    // 0x00 byte toggles between compressed and uncompressed UTF-16LE runs.
    // Before the fix everything after the marker was decoded as Latin-1, so any
    // Access string mixing ASCII with non-Latin1 text decoded as garbage.

    [Fact]
    public void DecodeText_CompressedOnly_DecodesAsOneBytePerChar()
    {
        var data = new byte[] { 0xFF, 0xFE, (byte)'A', (byte)'B', (byte)'C' };
        Assert.Equal("ABC", ByteUtil.DecodeText(data, 0, data.Length));
    }

    [Fact]
    public void DecodeText_ToggleIntoUncompressed_DecodesTrailingRunAsUtf16()
    {
        // "AB" compressed, 0x00 toggles, then UTF-16LE for a non-Latin1 char.
        var tail = Encoding.Unicode.GetBytes("\u30C6");          // Japanese 'te'
        var data = new byte[] { 0xFF, 0xFE, (byte)'A', (byte)'B', 0x00 }
            .Concat(tail).ToArray();

        Assert.Equal("AB\u30C6", ByteUtil.DecodeText(data, 0, data.Length));
    }

    [Fact]
    public void DecodeText_TogglesBackToCompressed()
    {
        // compressed "A" | toggle | UTF-16 non-Latin1 char | toggle | compressed "B".
        // The uncompressed run must use a char whose high byte is non-zero:
        // 0x00 anywhere in the stream is the mode toggle, so a character like
        // 'Z' (UTF-16LE 5A 00) cannot appear in an uncompressed run - Access
        // keeps such characters in a compressed run instead.
        var data = new byte[] { 0xFF, 0xFE, (byte)'A', 0x00 }
            .Concat(Encoding.Unicode.GetBytes("テ"))        // C6 30, no zero byte
            .Concat(new byte[] { 0x00, (byte)'B' })
            .ToArray();

        Assert.Equal("AテB", ByteUtil.DecodeText(data, 0, data.Length));
    }

    [Fact]
    public void DecodeText_NoMarker_IsPlainUtf16()
    {
        var data = Encoding.Unicode.GetBytes("Hello");
        Assert.Equal("Hello", ByteUtil.DecodeText(data, 0, data.Length));
    }

    [Fact]
    public void EncodeText_RefusesToCompressStringsContainingNul()
    {
        // Inside a compressed run 0x00 is the mode toggle, not a character, so a
        // string containing NUL must be written uncompressed or it decodes wrong.
        var encoded = ByteUtil.EncodeText("A\0B");
        Assert.False(encoded.Length >= 2 && encoded[0] == 0xFF && encoded[1] == 0xFE,
            "string containing NUL must not use the compressed form");
        Assert.Equal("A\0B", ByteUtil.DecodeText(encoded, 0, encoded.Length));
    }

    [Fact]
    public void MixedScriptMemo_SurvivesRoundTrip()
    {
        var value = "Resume ABC \u30C6\u30B9\u30C8 \u0431\u0440\u0430\u0442";
        Assert.Equal(value, RoundTripMemo(value));
    }

    // ── 2 & 3. LvRef format and inline threshold ─────────────────────────────
    // The type lives in the TOP TWO BITS of the 32-bit length:
    //   0x80000000 THIS_PAGE (inline, data at offset 12)
    //   0x40000000 OTHER_PAGE (single chunk)
    //   0x00000000 OTHER_PAGES (chunk chain)
    // The previous invented layout ([len:4][typeByte:1]) could neither read
    // Access-written long values nor produce refs Access could follow.

    [Fact]
    public void SmallMemo_RoundTrips()
    {
        const string value = "short memo";
        Assert.Equal(value, RoundTripMemo(value));
    }

    [Fact]
    public void LargeMemo_ConsumesMoreLvalPagesThanSmallMemo()
    {
        // Values up to the inline threshold live in the parent row (THIS_PAGE),
        // as Access does; larger ones move out to LVAL pages. Asserted as a
        // comparison because creating a table with a Memo column already
        // allocates some LVAL bookkeeping pages up front.
        RoundTripMemo("short memo");
        var smallPages = CountLvalPages(_path);

        RoundTripMemo(new string('x', 5000));
        var largePages = CountLvalPages(_path);

        Assert.True(largePages > smallPages,
            $"expected a 5000-char memo to use more LVAL pages than a short one (small={smallPages}, large={largePages})");
    }

    [Fact]
    public void LargeMemo_MovesToLvalPages_AndRoundTrips()
    {
        var value = new string('x', 5000);          // over the inline threshold
        Assert.Equal(value, RoundTripMemo(value));
        Assert.True(CountLvalPages(_path) > 0, "a large memo must occupy LVAL page(s)");
    }

    [Fact]
    public void MemoSpanningMultipleChunks_RoundTrips()
    {
        // Larger than one chunk row, so it exercises the OTHER_PAGES chain with
        // its [nextRow:1][nextPage:3] chunk prefixes.
        var value = new string('q', 20000);
        Assert.Equal(value, RoundTripMemo(value));
        Assert.True(CountLvalPages(_path) > 1, "a 20 KB memo must span multiple LVAL pages");
    }

    [Fact]
    public void EmptyMemo_RoundTripsAsEmpty()
    {
        Assert.Equal(string.Empty, RoundTripMemo(string.Empty));
    }

    [Fact]
    public void NullMemo_IsAbsentFromTheReadRow()
    {
        // A null long value is not written, so the column does not appear in the
        // row at all - callers must probe rather than index.
        Assert.False(RoundTripMemoRow(null).ContainsKey("M"));
    }

    // ── 4. LVAL page signature ───────────────────────────────────────────────
    // Access validates the ASCII signature "LVAL" at bytes 4-7 of a long-value
    // page (where ordinary data pages store their owning TDEF page number).
    // Without it Access reports the WHOLE DATABASE as corrupt, not just the value.

    [Fact]
    public void AllocatedLvalPages_CarryTheLvalSignature()
    {
        RoundTripMemo(new string('z', 5000));

        var pages = FindLvalPages(_path);
        Assert.NotEmpty(pages);
        foreach (var page in pages)
        {
            var sig = Encoding.ASCII.GetString(page, 4, 4);
            Assert.Equal("LVAL", sig);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string? RoundTripMemo(string? value)
    {
        var row = RoundTripMemoRow(value);
        return row.TryGetValue("M", out var v) ? v as string : null;
    }

    private Row RoundTripMemoRow(string? value)
    {
        if (File.Exists(_path)) File.Delete(_path);
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var t = db.CreateTable("T", new[]
            {
                new ColumnBuilder("Id", DataType.Long).Build(),
                new ColumnBuilder("M", DataType.Memo).Build(),
            });
            t.Insert(new Row { ["Id"] = 1, ["M"] = value });
        }
        using var reopen = Database.Open(_path);
        return reopen.GetTable("T").ReadAllRows()[0];
    }

    /// <summary>Every 4096-byte page whose bytes 4-7 spell "LVAL".</summary>
    private static List<byte[]> FindLvalPages(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var found = new List<byte[]>();
        for (var offset = 0; offset + 4096 <= bytes.Length; offset += 4096)
        {
            if (bytes[offset] != 0x01) continue;                    // data page
            if (Encoding.ASCII.GetString(bytes, offset + 4, 4) != "LVAL") continue;
            var page = new byte[4096];
            Array.Copy(bytes, offset, page, 0, 4096);
            found.Add(page);
        }
        return found;
    }

    private static int CountLvalPages(string path) => FindLvalPages(path).Count;
}
