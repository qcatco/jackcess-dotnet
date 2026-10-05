using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What the second review of qcatco/jackcess-dotnet#3 found, each seen failing first: a delete or update in a key
/// whose pages Access compressed, which changed the row and lost the key's change; a table an earlier version
/// registered outside the Tables container, whose name another table could then take; FindRowByEntry's scan
/// matching the table's first columns, not the index's; a root left holding one child; sort orders the engine
/// read but no test held it to; a definition refused after its pages were added; and the low-level key methods
/// searching a key in an order they do not write.
/// </summary>
public sealed class SecondReviewTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension = ".mdb")
    {
        var path = Path.Combine(Path.GetTempPath(), $"review3b_{Guid.NewGuid():N}{extension}");
        _paths.Add(path);
        return path;
    }

    private string CopyOf(string version, string filename)
    {
        string source = TestCorpus.File(version, filename) ?? throw new FileNotFoundException(filename);
        string path = NewPath(Path.GetExtension(filename));
        File.Copy(source, path);
        return path;
    }

    private static Column Long(string name) => new ColumnBuilder(name, DataType.Long).Build();

    private static Column Text(string name, int chars) => new ColumnBuilder(name, DataType.Text).MaxLength(chars).Build();

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    private static int TdefOf(PageFile file, string table)
        => (int)JetPages.Rows(file, JetFormat.PageSystemCatalog)
            .Single(r => (string?)Value(r.Values, "Name") == table && Value(r.Values, "Type") is short type && type == 1)
            .Values["Id"]!;

    // Where a column's definition keeps its sort order: four bytes from offset 11 of its 25.
    private static int SortOrderAt(byte[] tdef, int column) => 63 + ByteUtil.GetInt(tdef, 51) * 12 + column * 25 + 11;

    private static void SetSortOrder(PageFile file, int tdefPage, int column, byte[] order)
    {
        byte[] tdef = file.ReadPage(tdefPage);
        Array.Copy(order, 0, tdef, SortOrderAt(tdef, column), 4);
        file.WritePage(tdefPage, tdef);
    }

    private static void AssertKeyHoldsEveryRow(PageFile file, int tdef, int root)
        => Assert.Equal(JetPages.Rows(file, tdef).Select(r => (r.Page, r.Row)).OrderBy(p => p),
                        JetPages.LeafEntries(file, root).Select(e => (e.RowPage, e.Row)).OrderBy(p => p));

    // ── A key Access compressed ─────────────────────────────────────────────

    public static IEnumerable<object[]> CompressedKeyFiles() => new[]
    {
        new object[] { "V2000", "indexCodesV2000.mdb" },
        new object[] { "V2003", "indexCodesV2003.mdb" },
        new object[] { "V2007", "indexCodesV2007.accdb" },
    };

    // Access stores the bytes a key page's entries share once, so its full leaves hold more entries than fit written
    // out in full: Table14's primary key, 66,429 rows. A delete or an update there kept the row's change and lost the
    // key's.
    [Theory]
    [MemberData(nameof(CompressedKeyFiles))]
    public void A_key_Access_compressed_keeps_one_entry_per_row_through_a_delete_and_an_update(string version, string filename)
    {
        using var db = Database.Open(CopyOf(version, filename));
        var file = db.File;
        var table = db.GetTable("Table14");
        int tdef = TdefOf(file, "Table14");
        var key = table.Indexes.Single(ix => ix.IsPrimaryKey);
        int root = JetPages.IndexBlocks(file, tdef)[key.IndexDataNumber].Root;

        var leaf = JetPages.TreePages(file, root).Where(p => p.IsLeaf).OrderByDescending(p => p.Entries.Count).First();
        Assert.True(leaf.Entries.Sum(e => e.Key.Length + 4) > file.Format.PageSize - 480,
            "the fullest leaf would fit written out in full");
        var rows = JetPages.Rows(file, tdef).ToDictionary(r => (r.Page, r.Row), r => r.Values);
        string NameOf(JetPages.Entry e) => (string)rows[(e.RowPage, e.Row)]["name"]!;
        string deleted = NameOf(leaf.Entries[leaf.Entries.Count / 2]);
        string updated = NameOf(leaf.Entries[1]);

        table.DeleteRow("name", deleted);
        table.UpdateByPrimaryKey(updated, new Row { ["data"] = "changed" });

        AssertKeyHoldsEveryRow(file, tdef, root);
        Assert.Empty(JetPages.RuleBreaks(file, root, tdef));
        var cursor = db.GetTable("Table14").NewIndexCursor();
        Assert.Equal("changed", cursor.FindRowByPrimaryKey(updated)?["data"]);
        Assert.Null(cursor.FindRowByPrimaryKey(deleted));
    }

    // ── Pages that share a prefix ───────────────────────────────────────────

    // Keys sharing a long prefix pack a page, as Access packs it; a key sharing none with its neighbours undoes that.
    // An insert left a half of such a split page that still did not fit, and a delete, changing the entry a parent
    // copies to such a key, left the parent too full: each splits until every page fits.
    [Fact]
    public void A_key_sharing_no_prefix_with_a_packed_page_splits_it_until_every_page_fits()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Packed", new[] { Text("K", 255), Long("N") }, primaryKey: "K");
        string shared = "M" + new string('p', 245);
        for (int i = 0; i < 4000; i++)
            table.Insert(new Row { ["K"] = shared + i.ToString("D4"), ["N"] = i });
        var file = db.File;
        int tdef = TdefOf(file, "Packed");
        var block = JetPages.IndexBlocks(file, tdef).Single();
        Assert.True(JetPages.TreePages(file, block.Root).Count(p => p.IsLeaf) >= 14, "too few leaves");

        // Before them all, sharing nothing: the first leaf no longer fits, nor would its halves.
        table.Insert(new Row { ["K"] = "A", ["N"] = -1 });
        Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        AssertKeyHoldsEveryRow(file, tdef, block.Root);

        // The first leaf down to "A": the entry its parent copies shares nothing with the parent's others.
        var keyOf = JetPages.Rows(file, tdef).ToDictionary(r => (r.Page, r.Row), r => (string)r.Values["K"]!);
        var first = JetPages.ReadIndexPage(file, block.Root);
        while (!first.IsLeaf)
            first = JetPages.ReadIndexPage(file, first.Entries.Count > 0 ? first.Entries[0].SubPage : first.Tail);
        Assert.Equal("A", keyOf[(first.Entries[0].RowPage, first.Entries[0].Row)]);
        foreach (var e in first.Entries.Skip(1).Reverse())
            table.DeleteRow("K", keyOf[(e.RowPage, e.Row)]);

        Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        AssertKeyHoldsEveryRow(file, tdef, block.Root);
        var mapped = UsageMap.GetOwnedPages(file.ReadPage(block.UmapPage), block.UmapRow, file.Format, file);
        Assert.Equal(JetPages.TreePages(file, block.Root).Select(p => p.Number).OrderBy(n => n), mapped.OrderBy(n => n));
        Assert.Equal(-1, table.NewIndexCursor().FindRowByPrimaryKey("A")?["N"]);
        Assert.Equal(1234, table.NewIndexCursor().FindRowByPrimaryKey(shared + "1234")?["N"]);
    }

    // The engine's pages store the bytes their keys share once, as Access's do.
    [Fact]
    public void The_engine_s_key_pages_store_a_shared_prefix_once()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Packed", new[] { Text("K", 100), Long("N") }, primaryKey: "K");
        for (int i = 0; i < 300; i++)
            table.Insert(new Row { ["K"] = "PRODUCTION01.001.001." + i.ToString("D4"), ["N"] = i });
        var file = db.File;
        int root = JetPages.IndexBlocks(file, TdefOf(file, "Packed")).Single().Root;
        byte[] page = file.ReadPage(root);
        Assert.Equal(0x04, page[0]);                              // 300 such keys fit one leaf
        Assert.True(ByteUtil.GetUShort(page, 24) >= 20, $"a shared prefix of {ByteUtil.GetUShort(page, 24)} bytes");
        Assert.Equal(300, JetPages.LeafEntries(file, root).Count);
    }

    // ── A table an earlier version registered ──────────────────────────────

    // Versions before this one registered a table with ParentId 0, outside the Tables container.
    [Fact]
    public void A_table_an_earlier_version_registered_keeps_its_name()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Earlier", new[] { Long("Id") });
        var file = db.File;
        var row = JetPages.Rows(file, JetFormat.PageSystemCatalog).Single(r => (string?)Value(r.Values, "Name") == "Earlier");
        var parentId = db.GetTable("MSysObjects").Columns.Single(c => c.Name == "ParentId");
        byte[] page = file.ReadPage(row.Page);
        int rowStart = ByteUtil.GetUShort(page, file.Format.OffsetDataRowTable + row.Row * 2) & JetFormat.RowOffsetMask;
        ByteUtil.PutInt(page, rowStart + 2 + parentId.FixedDataOffset, 0);   // after the row's two-byte column count
        file.WritePage(row.Page, page);
        Assert.Equal(0, JetPages.Rows(file, JetFormat.PageSystemCatalog).Single(r => (string?)Value(r.Values, "Name") == "Earlier").Values["ParentId"]);
        int pages = file.PageCount;

        Assert.Throws<InvalidOperationException>(() => db.CreateTable("EARLIER", new[] { Long("Id") }));
        Assert.Equal(pages, file.PageCount);
        Assert.Single(db.ListTables(), n => string.Equals(n, "Earlier", StringComparison.OrdinalIgnoreCase));
    }

    // ── FindRowByEntry ──────────────────────────────────────────────────────

    // Through the index where its keys can be made (General - Legacy), by a scan of the index's own column where they
    // cannot (Access 2010's General order): either way every row is found by an index on a column other than the first.
    [Theory]
    [InlineData("V2003", "indexCodesV2003.mdb")]
    [InlineData("V2010", "indexCodesV2010.accdb")]
    public void FindRowByEntry_finds_every_row_by_an_index_on_a_later_column(string version, string filename)
    {
        using var db = Database.Open(CopyOf(version, filename));
        var table = db.GetTable("Table1_desc");
        string column = table.Indexes.Single(ix => ix.Name == "DataIndex").Columns.Single().Column.Name;
        Assert.NotEqual(table.Columns[0].Name, column);

        int found = 0;
        foreach (var row in table.ReadAllRows().Where(r => Value(r, column) is string s && s.Length > 0))
        {
            var hit = table.NewIndexCursor("DataIndex").FindRowByEntry(row[column]!);
            Assert.NotNull(hit);
            Assert.Equal(row[column], hit![column]);
            found++;
        }
        Assert.True(found >= 20, $"{found} rows looked up");
    }

    // ── Roots ───────────────────────────────────────────────────────────────

    // A key three levels deep, emptied so that the root's last node is down to one child before the root's other
    // nodes go: the root collapses into that node and on into its one leaf, never left holding a single child, and
    // the tree keeps Access's rules throughout, its pages all in its usage map.
    [Fact]
    public void A_deep_key_emptied_down_to_one_leaf_keeps_its_tree_whole()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Deep", new[] { Text("K", 200), Long("N") }, primaryKey: "K");
        for (int i = 0; i < 400; i++)
            table.Insert(new Row { ["K"] = $"{i:D4}" + new string((char)('a' + i % 26), 190), ["N"] = i });
        var file = db.File;
        int tdef = TdefOf(file, "Deep");
        var block = JetPages.IndexBlocks(file, tdef).Single();
        var root = JetPages.ReadIndexPage(file, block.Root);
        Assert.True(root.Level >= 2, "the key is not three levels deep");
        var lastNode = JetPages.ReadIndexPage(file, root.Tail);

        var keyOf = JetPages.Rows(file, tdef).ToDictionary(r => (r.Page, r.Row), r => (string)r.Values["K"]!);
        List<string> KeysUnder(int page) => JetPages.TreePages(file, page).Where(p => p.IsLeaf)
            .SelectMany(p => p.Entries).Select(e => keyOf[(e.RowPage, e.Row)]).ToList();
        var lastNodesOtherLeaves = lastNode.Entries.SelectMany(e => KeysUnder(e.SubPage)).ToList();
        var underOtherNodes = root.Entries.SelectMany(e => KeysUnder(e.SubPage)).ToList();

        int deleted = 0;
        foreach (string key in lastNodesOtherLeaves.Concat(underOtherNodes))
        {
            table.DeleteRow("K", key);
            if (++deleted % 20 == 0 || deleted > lastNodesOtherLeaves.Count + underOtherNodes.Count - 5)
            {
                Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
                var top = JetPages.ReadIndexPage(file, block.Root);
                Assert.True(top.IsLeaf || top.Entries.Count > 0, $"after {deleted} deletes the root holds a single child");
            }
        }
        Assert.True(JetPages.ReadIndexPage(file, block.Root).IsLeaf, "the root did not come down to the last leaf");
        AssertKeyHoldsEveryRow(file, tdef, block.Root);
        var mapped = UsageMap.GetOwnedPages(file.ReadPage(block.UmapPage), block.UmapRow, file.Format, file);
        Assert.Equal(JetPages.TreePages(file, block.Root).Select(p => p.Number).OrderBy(n => n), mapped.OrderBy(n => n));
    }

    // A key three levels deep whose root loses every node but its last: the root takes that node's entries and its
    // level, and stays a node.
    [Fact]
    public void A_root_that_collapses_into_a_node_takes_its_level()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Deep", new[] { Text("K", 200), Long("N") }, primaryKey: "K");
        for (int i = 0; i < 400; i++)
            table.Insert(new Row { ["K"] = $"{i:D4}" + new string((char)('a' + i % 26), 190), ["N"] = i });
        var file = db.File;
        int tdef = TdefOf(file, "Deep");
        var block = JetPages.IndexBlocks(file, tdef).Single();
        var root = JetPages.ReadIndexPage(file, block.Root);
        Assert.Equal(2, root.Level);

        var keyOf = JetPages.Rows(file, tdef).ToDictionary(r => (r.Page, r.Row), r => (string)r.Values["K"]!);
        foreach (var key in root.Entries.SelectMany(e => JetPages.TreePages(file, e.SubPage)).Where(p => p.IsLeaf)
                     .SelectMany(p => p.Entries).Select(e => keyOf[(e.RowPage, e.Row)]).ToList())
            table.DeleteRow("K", key);

        var top = JetPages.ReadIndexPage(file, block.Root);
        Assert.False(top.IsLeaf);
        Assert.Equal(1, top.Level);
        Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        AssertKeyHoldsEveryRow(file, tdef, block.Root);
    }

    // ── Sort orders ─────────────────────────────────────────────────────────

    // Versions before sort orders wrote 00 00 00 00 for a text column, and keyed it in General - Legacy.
    [Fact]
    public void A_text_key_with_no_sort_order_is_kept_in_General_Legacy()
    {
        string path = NewPath();
        using (var db = Database.Create(path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Coded", new[] { Text("Code", 10), Long("N") }, primaryKey: "Code");
            table.Insert(new Row { ["Code"] = "a", ["N"] = 1 });
            SetSortOrder(db.File, TdefOf(db.File, "Coded"), column: 0, new byte[4]);
        }
        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Coded");
            table.Insert(new Row { ["Code"] = "b", ["N"] = 2 });
            Assert.Equal(1, table.NewIndexCursor().FindRowByPrimaryKey("a")?["N"]);
            Assert.Equal(2, table.NewIndexCursor().FindRowByPrimaryKey("b")?["N"]);
        }
    }

    // Access 97's text keys are in an order of its own, which the engine does not write.
    [Fact]
    public void An_Access_97_text_key_is_not_written_and_the_file_is_left_as_it_was()
    {
        string path = CopyOf("V1997", "common1V1997.mdb");
        byte[] before = File.ReadAllBytes(path);
        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Table1");
            Assert.Contains(table.Indexes, ix => ix.IsPrimaryKey && ix.Columns.Single().Column.DataType == DataType.Text);
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["A"] = "new" }));
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // A Jet4 column cannot hold an Access 97 sort order: a table made from an Access 97 table's columns sorts in
    // General - Legacy.
    [Fact]
    public void A_table_made_from_an_Access_97_table_s_columns_sorts_in_General_Legacy()
    {
        IReadOnlyList<Column> columns;
        using (var old = Database.Open(CopyOf("V1997", "common1V1997.mdb")))
            columns = old.GetTable("Table2").Columns;
        Assert.Contains(columns, c => c.DataType == DataType.Text);

        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Copied", columns);
        byte[] tdef = db.File.ReadPage(TdefOf(db.File, "Copied"));
        for (int i = 0; i < columns.Count; i++)
            if (columns[i].DataType is DataType.Text or DataType.Memo)
                Assert.Equal("09040000", Convert.ToHexString(tdef, SortOrderAt(tdef, i), 4));
    }

    // The low-level methods a caller can reach directly refuse a key in an order they do not write, as Table does.
    [Fact]
    public void The_low_level_key_methods_refuse_a_text_key_in_another_order()
    {
        string path = NewPath();
        using (var db = Database.Create(path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Coded", new[] { Text("Code", 10), Long("N") }, primaryKey: "Code");
            table.Insert(new Row { ["Code"] = "a", ["N"] = 1 });
            SetSortOrder(db.File, TdefOf(db.File, "Coded"), column: 0, new byte[] { 0x09, 0x04, 0x00, 0x01 });
        }
        using (var db = Database.Open(path))
        {
            var definition = db.GetTable("Coded").Definition;
            var writer = new IndexWriter(db.File, new PageAllocator(db.File));
            Assert.Throws<NotSupportedException>(() => writer.FindRowByPrimaryKey(definition, "a"));
            Assert.Throws<NotSupportedException>(() => writer.InsertPrimaryKey(definition, "z", 0));
        }
    }

    // ── Refusals before pages ───────────────────────────────────────────────

    // What the table's definition cannot hold is found before a page is added: a key of eleven columns, and names
    // too long for one TDEF page.
    [Fact]
    public void A_table_its_definition_cannot_hold_is_refused_before_a_page_is_added()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        int pages = db.File.PageCount;

        var eleven = Enumerable.Range(0, 11).Select(i => Long($"K{i}")).ToArray();
        Assert.Throws<InvalidOperationException>(() => db.CreateTable("Wide key", eleven, eleven.Select(c => c.Name).ToList()));
        Assert.Equal(pages, db.File.PageCount);

        var named = Enumerable.Range(0, 120).Select(i => Long($"{i:D3}" + new string('x', 60))).ToArray();
        Assert.Throws<NotSupportedException>(() => db.CreateTable("Long names", named));
        Assert.Equal(pages, db.File.PageCount);
    }
}
