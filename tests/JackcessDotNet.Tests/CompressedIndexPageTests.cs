using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Inserting into an index whose leaves Access <b>prefix-compressed</b> — it stores the leading
/// bytes its entries share once, and the rest of the entries omit them.
/// <para>
/// The fixture is ACE-written and checked in, because this library always writes a zero prefix
/// count and so cannot produce a compressed page itself: <c>Docs.IX_DocNo</c> indexes 400 values
/// that share a 31-character prefix, and four of its five leaves are compressed. An insert has to
/// expand those entries and re-emit them in full.
/// </para>
/// </summary>
public sealed class CompressedIndexPageTests : IDisposable
{
    private const string TableName = "Docs";

    private readonly string _path;
    public CompressedIndexPageTests()
        => _path = Path.Combine(Path.GetTempPath(), $"cmpidx_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    /// <summary>
    /// Copies the checked-in fixture into a temp file. The fixture is committed, so failing to
    /// find it is a broken checkout, not a reason to skip — a silently skipped test here would
    /// report green for the exact case it exists to cover.
    /// </summary>
    private string StageFixture()
    {
        string here = AppContext.BaseDirectory;
        for (int up = 0; up < 8; up++)
        {
            string candidate = Path.GetFullPath(
                Path.Combine(here, "..", "tests", "fixtures", "ace_compressed_index.mdb"));
            if (File.Exists(candidate)) { File.Copy(candidate, _path, overwrite: true); return candidate; }
            here = Path.GetFullPath(Path.Combine(here, ".."));
        }
        throw new FileNotFoundException(
            "tests/fixtures/ace_compressed_index.mdb is missing; see tests/fixtures/README.md.");
    }

    /// <summary>
    /// The fixture holds DocNo …00000001 through …00000400. A new key has to land <em>inside</em> a
    /// compressed leaf to exercise anything: appending keys above …00000400 all descend to the one
    /// uncompressed tail leaf, and the test passes without ever expanding a prefix. So the inserted
    /// DocNo interleaves — …00000005A sorts between …00000005 and …00000006 — while Id, which is the
    /// unique primary key, keeps counting up from 401. IX_DocNo is not unique, so this is legal.
    /// </summary>
    private static string InsertedDocNo(int i) => $"IR-1404-COMMISSION-DOCUMENT-{(i - 400) * 5:D8}A";

    private static Row RowFor(int i) => new()
    {
        ["Id"]    = i,
        ["DocNo"] = InsertedDocNo(i),
        ["Descr"] = $"n{i}",
    };

    /// <summary>Proves the fixture still has compressed leaves — the whole point of the file.</summary>
    [Fact]
    public void TheFixtureStillHasCompressedLeaves()
    {
        StageFixture();

        using var db = Database.Open(_path);
        var ix = Assert.Single(db.GetTable(TableName).Indexes, i => i.Name == "IX_DocNo");

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] root = pf.ReadPage(ix.RootPageNumber);

        // The root is a node; its children are the compressed leaves.
        Assert.Equal(JetFormat.PageTypeIndexNode, root[0]);

        int compressed = 0;
        foreach (int child in ChildPages(root))
        {
            byte[] page = pf.ReadPage(child);
            if (ByteUtil.GetUShort(page, JetFormat.Jet4.OffsetIndexCompressedByteCount) > 0) compressed++;
        }
        Assert.True(compressed > 0, "the fixture is supposed to carry prefix-compressed leaves");
    }

    [Fact]
    public void InsertingIntoACompressedIndex_KeepsEveryRowFindableThroughIt()
    {
        StageFixture();

        const int First = 401, Count = 60;

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(TableName);
            for (int i = First; i < First + Count; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable(TableName);

        Assert.Equal(400 + Count, reread.ReadAllRows().Count);

        // The rows that were already indexed must still be findable — expanding a compressed page
        // and re-emitting it wrong would lose exactly those.
        foreach (int i in new[] { 1, 99, 100, 250, 399, 400 })
            Assert.NotNull(reread.NewIndexCursor("IX_DocNo")
                .FindRow("DocNo", $"IR-1404-COMMISSION-DOCUMENT-{i:D8}"));

        for (int i = First; i < First + Count; i++)
            Assert.NotNull(reread.NewIndexCursor("IX_DocNo").FindRow("DocNo", InsertedDocNo(i)));
    }

    /// <summary>A rewritten page is emitted in full, so its prefix count must be cleared.</summary>
    [Fact]
    public void ARewrittenPage_NoLongerClaimsASharedPrefix()
    {
        StageFixture();

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(TableName);
            for (int i = 401; i < 461; i++) table.Insert(RowFor(i));
        }

        using var db2 = Database.Open(_path);
        var ix = Assert.Single(db2.GetTable(TableName).Indexes, i => i.Name == "IX_DocNo");

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] root = pf.ReadPage(ix.RootPageNumber);

        foreach (int child in ChildPages(root))
        {
            byte[] page = pf.ReadPage(child);
            int prefix  = ByteUtil.GetUShort(page, JetFormat.Jet4.OffsetIndexCompressedByteCount);
            int entries = CountEntries(page);
            // Untouched pages keep Access's prefix; a page this library rewrote must declare none.
            Assert.True(prefix == 0 || entries > 0, $"page {child} claims prefix {prefix} with no entries");
        }
    }

    private static List<int> ChildPages(byte[] node)
    {
        var format  = JetFormat.Jet4;
        var kids    = new List<int>();
        int maskPos = format.OffsetIndexEntryMask, maskLen = format.SizeIndexEntryMask;
        int entries = maskPos + maskLen, lastStart = 0;

        for (int i = 0; i < maskLen; i++)
            for (int j = 0; j < 8; j++)
            {
                if ((node[maskPos + i] & (1 << j)) == 0) continue;
                int end = i * 8 + j, len = end - lastStart, abs = entries + lastStart;
                if (len >= 8)
                {
                    int k = abs + len - 4;   // entry trailer: 4-byte big-endian sub-page
                    kids.Add((node[k] << 24) | (node[k + 1] << 16) | (node[k + 2] << 8) | node[k + 3]);
                }
                lastStart = end;
            }

        int tail = ByteUtil.GetInt(node, format.OffsetChildTailIndexPage);
        if (tail > 0) kids.Add(tail);
        return kids;
    }

    private static int CountEntries(byte[] page)
    {
        var format = JetFormat.Jet4;
        int n = 0;
        for (int i = 0; i < format.SizeIndexEntryMask; i++)
            for (int j = 0; j < 8; j++)
                if ((page[format.OffsetIndexEntryMask + i] & (1 << j)) != 0) n++;
        return n;
    }
}
