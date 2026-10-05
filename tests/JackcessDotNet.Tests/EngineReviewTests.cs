using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What the review of qcatco/jackcess-dotnet#3 found, each seen failing first: a table registered in a file whose
/// catalog sorts text in an order the engine does not key; an index root that moved under a writer holding the old
/// one; text columns created with no sort order, which Access then keys in another collation; a primary key an
/// earlier version wrote; a delete or insert that changed a table before finding its key could not be kept; a name a
/// query already has; and the global usage map read again for every page appended.
/// </summary>
public sealed class EngineReviewTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension = ".mdb")
    {
        var path = Path.Combine(Path.GetTempPath(), $"review3_{Guid.NewGuid():N}{extension}");
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

    private static Column[] OneColumn() => new[] { new ColumnBuilder("Id", DataType.Long).Build() };

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    private static int TdefOf(PageFile file, string table)
        => (int)JetPages.Rows(file, JetFormat.PageSystemCatalog)
            .Single(r => (string?)Value(r.Values, "Name") == table && Value(r.Values, "Type") is short type && type == 1)
            .Values["Id"]!;

    // ── Sort orders ─────────────────────────────────────────────────────────

    // Access-made files whose catalog keeps names in the General Legacy order (Access 2000 to 2007, and a 2010 file).
    public static IEnumerable<object[]> GeneralLegacyFiles() => new[]
    {
        new object[] { "V2000", "common1V2000.mdb" },
        new object[] { "V2003", "indexV2003.mdb" },
        new object[] { "V2003", "queryV2003.mdb" },
        new object[] { "V2007", "common1V2007.accdb" },
        new object[] { "V2010", "calcFieldV2010.accdb" },
    };

    // Access 2010 files whose catalog keeps names in Access 2010's General order, whose keys the engine does not write.
    public static IEnumerable<object[]> GeneralOrderFiles() => new[]
    {
        new object[] { "V2010", "indexV2010.accdb" },
        new object[] { "V2010", "common1V2010.accdb" },
    };

    // The keys Access itself wrote for its catalog rows are the reference: the engine's key for each row must be the
    // same bytes, or Access cannot find by name a table the engine registers.
    [Theory]
    [MemberData(nameof(GeneralLegacyFiles))]
    public void The_engine_keys_every_catalog_row_as_Access_keyed_it(string version, string filename)
    {
        using var db = Database.Open(CopyOf(version, filename));
        var file = db.File;
        var parentIdName = db.GetTable("MSysObjects").Indexes.Single(ix => ix.Name == "ParentIdName");
        var block = JetPages.IndexBlocks(file, JetFormat.PageSystemCatalog)[parentIdName.IndexDataNumber];
        var accessKeys = JetPages.LeafEntries(file, block.Root).ToDictionary(e => (e.RowPage, e.Row), e => e.Hex);

        int compared = 0;
        foreach (var row in JetPages.Rows(file, JetFormat.PageSystemCatalog))
        {
            if (!accessKeys.TryGetValue((row.Page, row.Row), out var accessKey)) continue;   // an overflow row's slot
            var values = parentIdName.Columns.Select(c => Value(row.Values, c.Column.Name)).ToArray();
            Assert.Equal(accessKey, Convert.ToHexString(IndexKeys.Encode(parentIdName.Columns, values)));
            compared++;
        }
        Assert.True(compared >= 10, $"{compared} rows compared");
    }

    [Theory]
    [MemberData(nameof(GeneralOrderFiles))]
    public void A_file_whose_catalog_sorts_in_the_newer_General_order_is_not_given_a_table(string version, string filename)
    {
        string path = CopyOf(version, filename);
        byte[] before = File.ReadAllBytes(path);
        using (var db = Database.Open(path))
        {
            int pages = db.File.PageCount;
            Assert.Throws<NotSupportedException>(() => db.CreateTable("EngineMade", OneColumn()));
            Assert.Equal(pages, db.File.PageCount);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void The_engines_text_and_memo_columns_sort_in_General_Legacy_as_Access_writes_it()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Texts", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
        });

        byte[] tdef = db.File.ReadPage(TdefOf(db.File, "Texts"));
        int columnDefs = 63 + ByteUtil.GetInt(tdef, 51) * 12;
        string SortOrder(int column) => Convert.ToHexString(tdef, columnDefs + column * 25 + 11, 4);
        Assert.Equal("00000000", SortOrder(0));
        Assert.Equal("09040000", SortOrder(1));   // LCID 1033, version 0: General Legacy
        Assert.Equal("09040000", SortOrder(2));
    }

    // An index on text kept in an order the engine does not key is not used to find rows: the table is scanned, so
    // every row is found. Access 2010 keyed these tables' text, which holds the characters whose keys differ most
    // between the orders, in its General order.
    [Fact]
    public void A_text_index_in_the_newer_General_order_is_not_trusted_to_find_rows()
    {
        using var db = Database.Open(CopyOf("V2010", "indexCodesV2010.accdb"));
        int found = 0;
        foreach (string name in db.ListTables())
        {
            var table = db.GetTable(name);
            var rows = table.ReadAllRows();
            if (rows.Count > 1000) continue;   // each row is found by a scan of the table
            foreach (var index in table.Indexes.Where(ix => ix.Columns.Count == 1 && ix.Columns[0].Column.DataType == DataType.Text))
            {
                string column = index.Columns[0].Column.Name;
                foreach (var row in rows.Where(r => Value(r, column) is string s && s.Length > 0))
                {
                    var hit = table.NewIndexCursor(index.Name).FindRow(column, row[column]!);
                    Assert.NotNull(hit);
                    Assert.Equal(row[column], hit![column]);
                    found++;
                }
            }
        }
        Assert.True(found > 0, "no single-column text index with rows was found to test");
    }

    // ── Index roots ─────────────────────────────────────────────────────────

    // Each table registers several ACE rows; a root split by one of them moved, and the next went into the old root.
    [Fact]
    public void Many_tables_registered_in_an_Access_made_file_keep_its_catalog_indexes_whole()
    {
        using var db = Database.Open(CopyOf("V2003", "indexV2003.mdb"));
        for (int i = 0; i < 150; i++)
            db.CreateTable($"Registered {i:D3}", OneColumn());

        var file = db.File;
        foreach (int tdef in new[] { JetFormat.PageSystemCatalog, TdefOf(file, "MSysACEs") })
            foreach (var block in JetPages.IndexBlocks(file, tdef))
                Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        Assert.Equal(150, db.ListTables().Count(n => n.StartsWith("Registered ")));
    }

    // Two Table objects for one table each hold its key's root: a split through one must not mislead the other.
    [Fact]
    public void A_split_made_through_one_table_object_does_not_mislead_another()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Keyed", new[] { new ColumnBuilder("Id", DataType.Long).Build() }, primaryKey: "Id");
        var first = db.GetTable("Keyed");
        var second = db.GetTable("Keyed");

        for (int i = 1; i <= 1500; i++)            // splits the root several times
            first.Insert(new Row { ["Id"] = i * 2 });
        for (int i = 1; i <= 1500; i++)
            second.Insert(new Row { ["Id"] = i * 2 + 1 });

        var file = db.File;
        int tdef = TdefOf(file, "Keyed");
        var block = JetPages.IndexBlocks(file, tdef).Single();
        Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        Assert.Equal(3000, JetPages.LeafEntries(file, block.Root).Count);
    }

    // ── A key an earlier version wrote ─────────────────────────────────────

    // Earlier versions wrote a node's entries with row pointer 0, an entry for every child, no tail, and 0xFFFFFFFF for
    // no page. Rewritten here into that form from a tree this version built.
    private static void ToEarlierForm(PageFile file, int root)
    {
        var node = JetPages.ReadIndexPage(file, root);
        Assert.False(node.IsLeaf);
        var children = node.Entries.Select(e => e.SubPage).Append(node.Tail).ToList();
        byte[] raw = file.ReadPage(root);
        Array.Clear(raw, 27, raw.Length - 27);
        int end = 0;
        foreach (int child in children)
        {
            byte[] key = JetPages.ReadIndexPage(file, child).Entries[^1].Key;
            Array.Copy(key, 0, raw, 480 + end, key.Length);
            end += key.Length + 4;                    // row pointer left 0
            raw[480 + end++] = (byte)(child >> 24);
            raw[480 + end++] = (byte)(child >> 16);
            raw[480 + end++] = (byte)(child >> 8);
            raw[480 + end++] = (byte)child;
            raw[27 + end / 8] |= (byte)(1 << (end % 8));
        }
        ByteUtil.PutShort(raw, 2, (short)(4096 - 480 - end));
        ByteUtil.PutInt(raw, 20, -1);                 // no tail
        raw[26] = 0;                                  // no level
        file.WritePage(root, raw);
        foreach (int child in children)
        {
            var page = JetPages.ReadIndexPage(file, child);
            byte[] leaf = file.ReadPage(child);
            if (page.Prev == 0) ByteUtil.PutInt(leaf, 12, -1);
            if (page.Next == 0) ByteUtil.PutInt(leaf, 16, -1);
            ByteUtil.PutInt(leaf, 4, 0);              // no TDEF named
            file.WritePage(child, leaf);
        }
    }

    [Fact]
    public void A_primary_key_an_earlier_version_wrote_keeps_one_entry_per_row_through_deletes_and_updates()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Keyed", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(20).Build(),
        }, primaryKey: "Id");
        for (int i = 1; i <= 1200; i++)
            table.Insert(new Row { ["Id"] = i, ["Txt"] = $"v{i}" });
        var file = db.File;
        int tdef = TdefOf(file, "Keyed");
        int root = JetPages.IndexBlocks(file, tdef).Single().Root;
        var firstLeaf = JetPages.ReadIndexPage(file, JetPages.ReadIndexPage(file, root).Entries[0].SubPage);
        var lastOfFirstLeaf = firstLeaf.Entries[^1];
        int firstLeafLast = (int)JetPages.Rows(file, tdef)
            .Single(r => r.Page == lastOfFirstLeaf.RowPage && r.Row == lastOfFirstLeaf.Row).Values["Id"]!;
        ToEarlierForm(file, root);

        var reopened = db.GetTable("Keyed");
        reopened.DeleteRow("Id", firstLeafLast);   // the last key of a leaf, whose node entry held row pointer 0
        reopened.UpdateByPrimaryKey(700, new Row { ["Txt"] = "updated" });
        reopened.Insert(new Row { ["Id"] = 5000, ["Txt"] = "new" });

        var rows = JetPages.Rows(file, tdef);
        var entries = JetPages.LeafEntries(file, root);
        Assert.Equal(rows.Select(r => (r.Page, r.Row)).OrderBy(p => p), entries.Select(e => (e.RowPage, e.Row)).OrderBy(p => p));
        Assert.Equal("updated", db.GetTable("Keyed").NewIndexCursor().FindRowByPrimaryKey(700)?["Txt"]);
    }

    // ── A key that cannot be kept ──────────────────────────────────────────

    // A text key in Access 2010's General order: the engine cannot write its entries, so it changes nothing.
    [Fact]
    public void A_table_whose_key_the_engine_cannot_write_is_left_as_it_was()
    {
        string path = NewPath();
        using (var db = Database.Create(path, JetVersion.Jet4))
        {
            var table = db.CreateTable("Coded", new[]
            {
                new ColumnBuilder("Code", DataType.Text).MaxLength(10).Build(),
                new ColumnBuilder("N", DataType.Long).Build(),
            }, primaryKey: "Code");
            foreach (var code in new[] { "a", "b", "c" })
                table.Insert(new Row { ["Code"] = code, ["N"] = 1 });

            // The key column now says Access 2010's General order (version 1).
            int tdef = TdefOf(db.File, "Coded");
            byte[] page = db.File.ReadPage(tdef);
            int columnDefs = 63 + ByteUtil.GetInt(page, 51) * 12;
            page[columnDefs + 0 * 25 + 14] = 1;
            db.File.WritePage(tdef, page);
        }

        using (var db = Database.Open(path))
        {
            var table = db.GetTable("Coded");
            Assert.Throws<NotSupportedException>(() => table.DeleteRow("Code", "b"));
            Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["Code"] = "d", ["N"] = 1 }));
            Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey("a", new Row { ["N"] = 2 }));
            var rows = db.GetTable("Coded").ReadAllRows();
            Assert.Equal(new[] { "a", "b", "c" }, rows.Select(r => (string)r["Code"]!).OrderBy(c => c));
            Assert.All(rows, r => Assert.Equal(1, r["N"]));
        }
    }

    // ── Names ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_table_cannot_take_a_name_a_query_has_and_a_refusal_leaves_no_pages()
    {
        using var db = Database.Open(CopyOf("V2003", "queryV2003.mdb"));
        string query = JetPages.Rows(db.File, JetFormat.PageSystemCatalog)
            .Where(r => Value(r.Values, "Type") is short type && type == 5)
            .Select(r => (string)r.Values["Name"]!)
            .First(n => !n.StartsWith("~"));
        int pages = db.File.PageCount;

        Assert.Throws<InvalidOperationException>(() => db.CreateTable(query.ToUpperInvariant(), OneColumn()));
        Assert.Equal(pages, db.File.PageCount);
    }

    // ── The global usage map ────────────────────────────────────────────────

    [Fact]
    public void Appending_pages_reads_the_header_and_the_free_page_map_once()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var allocator = new PageAllocator(db.File);
        allocator.AllocatePage();
        long before = db.File.ReadCount;

        for (int i = 0; i < 200; i++)
            allocator.AllocatePage();

        Assert.True(db.File.ReadCount - before <= 2, $"{db.File.ReadCount - before} pages read for 200 appended");
        var (_, free, _) = JetPages.GlobalFreePages(db.File, db.File.PageCount);
        Assert.Empty(free.Where(p => p >= 50));
    }
}
