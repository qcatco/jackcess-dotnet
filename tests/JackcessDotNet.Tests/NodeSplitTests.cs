using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Growing an index past two levels. When a leaf splits, its parent takes one more child entry;
/// once the root node is full that entry has nowhere to go, and the root itself has to split into
/// two nodes under a new root — a third level.
/// <para>
/// What fills a page is the size of the index <em>key</em>, not the row, so these tests index a
/// wide text column: ~7 entries per leaf and ~7 children per node means the root node fills within
/// roughly 50 rows, instead of the ~100,000 a 4-byte <c>Long</c> key would need.
/// </para>
/// </summary>
public sealed class NodeSplitTests : IDisposable
{
    private readonly string _path;
    public NodeSplitTests()
        => _path = Path.Combine(Path.GetTempPath(), $"nodesplit_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private static IReadOnlyList<Column> Columns() => new[]
    {
        new ColumnBuilder("Key", DataType.Text).MaxLength(200).Build(),
        new ColumnBuilder("Id",  DataType.Long).Build(),
    };

    private static string KeyFor(int i) => $"{i:D6}" + new string('x', 190);
    private static Row    RowFor(int i) => new() { ["Key"] = KeyFor(i), ["Id"] = i };

    /// <summary>Creates the table and reopens, so inserts take the general (all-indexes) path.</summary>
    private Database CreateAndReopen()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", Columns(), primaryKey: "Key");
        return Database.Open(_path);
    }

    [Fact]
    public void PastTheRootNodesCapacity_TheTreeGrowsAThirdLevel()
    {
        const int Rows = 400;   // several times what a two-level tree of these keys can hold

        using var db = CreateAndReopen();
        var table = db.GetTable("T");
        for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));

        var reread = db.GetTable("T");
        Assert.Equal(Rows, reread.ReadAllRows().Count);

        var ix = Assert.Single(reread.Indexes, i => i.IsPrimaryKey);

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);
        byte[] root = pf.ReadPage(ix.RootPageNumber);
        Assert.Equal(JetFormat.PageTypeIndexNode, root[0]);

        // Depth 3: the root's children must themselves be nodes, not leaves.
        var children = ChildPages(root, pf);
        Assert.NotEmpty(children);
        Assert.All(children, c => Assert.Equal(JetFormat.PageTypeIndexNode, pf.ReadPage(c)[0]));
    }

    [Fact]
    public void PastTheRootNodesCapacity_EveryRowIsStillFoundThroughTheIndex()
    {
        const int Rows = 400;

        using (var db = CreateAndReopen())
        {
            var table = db.GetTable("T");
            for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));
        }

        // Reopen so the seeks read the tree back off the patched TDEF — and only after the first
        // handle is closed, since two Database instances cannot hold the same file.
        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("T");

        for (int i = 0; i < Rows; i++)
            Assert.NotNull(reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(KeyFor(i)));
    }

    /// <summary>Every leaf must stay reachable by following the sibling chain from the leftmost.</summary>
    [Fact]
    public void PastTheRootNodesCapacity_TheLeafChainStillCoversEveryEntry()
    {
        const int Rows = 400;

        using (var db = CreateAndReopen())
        {
            var table = db.GetTable("T");
            for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var ix = Assert.Single(reopened.GetTable("T").Indexes, i => i.IsPrimaryKey);

        using var pf = new PageFile(_path, JetFormat.Jet4, FileMode.Open, FileAccess.Read);

        // Descend the leftmost spine to the first leaf, then walk `next` to the end.
        int page = ix.RootPageNumber;
        while (pf.ReadPage(page)[0] == JetFormat.PageTypeIndexNode)
            page = ChildPages(pf.ReadPage(page), pf)[0];

        int total = 0, visited = 0;
        var seen = new HashSet<int>();
        while (page > 0 && seen.Add(page))
        {
            byte[] leaf = pf.ReadPage(page);
            Assert.Equal(JetFormat.PageTypeIndexLeaf, leaf[0]);
            total += CountEntries(leaf);
            visited++;
            page = ByteUtil.GetInt(leaf, JetFormat.Jet4.OffsetNextIndexPage);
        }

        Assert.True(visited > 2, $"expected several leaves in a three-level tree, walked {visited}");
        Assert.Equal(Rows, total);
    }

    /// <summary>
    /// Well past a single node split — 1200 of these keys need the root to split more than once,
    /// so this covers splits propagating repeatedly rather than the one-off the tests above see.
    /// </summary>
    [Fact]
    public void ManyNodeSplits_InsertWithoutThrowingAndStayFindable()
    {
        const int Rows = 1200;

        using (var db = CreateAndReopen())
        {
            var table = db.GetTable("T");
            // No try/catch: none of these may throw. Before node splits were written, the insert
            // at roughly row 50 threw NotSupportedException.
            for (int i = 0; i < Rows; i++) table.Insert(RowFor(i));
        }

        using var reopened = Database.Open(_path);
        var reread = reopened.GetTable("T");

        Assert.Equal(Rows, reread.ReadAllRows().Count);
        for (int i = 0; i < Rows; i++)
            Assert.NotNull(reread.NewIndexCursor("PrimaryKey").FindRowByPrimaryKey(KeyFor(i)));
    }

    private static List<int> ChildPages(byte[] node, PageFile pf)
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
