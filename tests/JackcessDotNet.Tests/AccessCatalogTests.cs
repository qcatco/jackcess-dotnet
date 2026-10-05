using System.IO;
using System.Text;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// A table the engine creates is one Access can see and use (qcatco/exportmdb.interop#22): registered in
/// MSysObjects as Access registers its own - the row, its entries in MSysObjects' indexes, its permissions in
/// MSysACEs - and stored as Access stores one. The files of the test corpus are the reference: Access made their
/// tables, so a table the engine adds beside one must match it. ACE itself checks the same in ExportMdb.Interop's
/// classic-build tests.
/// </summary>
public sealed class AccessCatalogTests : IDisposable
{
    private readonly List<string> _paths = new();

    public void Dispose()
    {
        foreach (var path in _paths)
            if (File.Exists(path)) File.Delete(path);
    }

    private string NewPath(string extension = ".mdb")
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog_{Guid.NewGuid():N}{extension}");
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

    // Access-made files of the corpus whose tables Access created under the Admin user with the Tables
    // container's permissions, from Access 2000 to 2010.
    public static IEnumerable<object[]> AccessMadeFiles() => new[]
    {
        new object[] { "V2000", "common1V2000.mdb" },
        new object[] { "V2000", "indexV2000.mdb" },
        new object[] { "V2003", "common1V2003.mdb" },
        new object[] { "V2003", "delV2003.mdb" },
        new object[] { "V2003", "indexCodesV2003.mdb" },
        new object[] { "V2007", "common1V2007.accdb" },
        // An Access 2010 file whose catalog sorts names in General - Legacy: one in Access 2010's General order is
        // refused (EngineReviewTests).
        new object[] { "V2010", "calcFieldV2010.accdb" },
    };

    private static int TdefOf(PageFile file, string table)
        => (int)JetPages.Rows(file, JetFormat.PageSystemCatalog)
            .Single(r => (string?)Value(r.Values, "Name") == table && Value(r.Values, "Type") is short type && type == 1)
            .Values["Id"]!;

    private static object? Value(Row row, string column) => row.TryGetValue(column, out var value) ? value : null;

    private static List<string> Aces(PageFile file, int objectId)
        => JetPages.Rows(file, TdefOf(file, "MSysACEs"))
            .Where(r => Value(r.Values, "ObjectId") is int id && id == objectId)
            .Select(r => $"{Convert.ToHexString((byte[])r.Values["SID"]!)}/{r.Values["ACM"]}/{Value(r.Values, "FInheritable") is true}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    [Theory]
    [MemberData(nameof(AccessMadeFiles))]
    public void A_new_table_gets_the_catalog_row_and_permissions_Access_gave_its_own(string version, string filename)
    {
        using var db = Database.Open(CopyOf(version, filename));
        var file = db.File;
        var accessTable = JetPages.Rows(file, JetFormat.PageSystemCatalog)
            .First(r => Value(r.Values, "Type") is short type && type == 1 && Value(r.Values, "Flags") is int flags && flags == 0);

        db.CreateTable("EngineMade", OneColumn());

        var mine = JetPages.Rows(file, JetFormat.PageSystemCatalog).Single(r => (string?)Value(r.Values, "Name") == "EngineMade");
        Assert.Equal(accessTable.Values["ParentId"], mine.Values["ParentId"]);
        Assert.Equal(accessTable.Values["Owner"], Value(mine.Values, "Owner"));
        Assert.Equal(0, mine.Values["Flags"]);
        Assert.Equal((short)1, mine.Values["Type"]);
        Assert.Equal(Aces(file, (int)accessTable.Values["Id"]!), Aces(file, (int)mine.Values["Id"]!));
    }

    // The entries the new tables add to each catalog index are exactly the rows they add: compared as what is
    // new, since an Access-made file can index a row at a slot it has since moved (an overflow row).
    [Theory]
    [MemberData(nameof(AccessMadeFiles))]
    public void Every_row_a_new_table_adds_to_the_catalog_has_an_entry_in_every_catalog_index(string version, string filename)
    {
        using var db = Database.Open(CopyOf(version, filename));
        var file = db.File;
        var catalogs = new[] { JetFormat.PageSystemCatalog, TdefOf(file, "MSysACEs") };
        var before = catalogs.ToDictionary(tdef => tdef, tdef => Snapshot(file, tdef));

        foreach (string name in new[] { "First", "Second", "Third" })
            db.CreateTable(name, OneColumn());

        foreach (int tdef in catalogs)
        {
            var (rowsBefore, entriesBefore) = before[tdef];
            var (rowsAfter, entriesAfter) = Snapshot(file, tdef);
            var newRows = rowsAfter.Except(rowsBefore).OrderBy(p => p).ToList();
            Assert.NotEmpty(newRows);
            foreach (var (block, entries) in entriesAfter)
                Assert.Equal(newRows, entries.Except(entriesBefore[block]).OrderBy(p => p).ToList());
        }
    }

    private static (HashSet<(int, int)> Rows, Dictionary<int, HashSet<(int, int)>> Entries) Snapshot(PageFile file, int tdef)
    {
        var rows = JetPages.Rows(file, tdef).Select(r => (r.Page, r.Row)).ToHashSet();
        var entries = JetPages.IndexBlocks(file, tdef)
            .Select((block, i) => (i, JetPages.LeafEntries(file, block.Root).Select(e => (e.RowPage, e.Row)).ToHashSet()))
            .ToDictionary(x => x.i, x => x.Item2);
        return (rows, entries);
    }

    [Theory]
    [MemberData(nameof(AccessMadeFiles))]
    public void Access_finds_a_new_table_by_name_through_ParentIdName(string version, string filename)
    {
        using var db = Database.Open(CopyOf(version, filename));
        db.CreateTable("Found By Name", OneColumn());

        var file = db.File;
        var mine = JetPages.Rows(file, JetFormat.PageSystemCatalog).Single(r => (string?)Value(r.Values, "Name") == "Found By Name");
        var parentIdName = db.GetTable("MSysObjects").Indexes.Single(ix => ix.Name == "ParentIdName");
        var found = new IndexReader(file, parentIdName)
            .FindRowPointersForEntry(new object?[] { mine.Values["ParentId"], "Found By Name" })
            .Select(p => (RowPointer.Page(p), RowPointer.Row(p)))
            .ToList();
        Assert.Equal(new[] { (mine.Page, mine.Row) }, found);
    }

    [Fact]
    public void The_catalog_indexes_keep_Access_rules_as_they_split()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        for (int i = 0; i < 400; i++)
            db.CreateTable($"Table {i:D4} with a longer name", OneColumn());

        var file = db.File;
        int splits = 0;
        foreach (int tdef in new[] { JetFormat.PageSystemCatalog, TdefOf(file, "MSysACEs") })
        {
            foreach (var block in JetPages.IndexBlocks(file, tdef))
            {
                Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
                var tree = JetPages.TreePages(file, block.Root).Select(p => p.Number).ToHashSet();
                var used = UsageMap.GetOwnedPages(file.ReadPage(block.UmapPage), block.UmapRow, file.Format, file).ToHashSet();
                Assert.Subset(used, tree);
                if (tree.Count > 1) splits++;
            }
        }
        Assert.True(splits >= 2, $"{splits} catalog indexes split");
        Assert.Equal(400, db.ListTables().Count);
    }

    [Fact]
    public void A_primary_key_keeps_Access_rules_through_its_splits()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Keyed", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(20).Build(),
        }, primaryKey: "Id");
        var keys = Enumerable.Range(1, 5000).Select(i => (int)((i * 7919L) % 100003)).ToList();
        foreach (int key in keys)
            table.Insert(new Row { ["Id"] = key, ["Txt"] = $"v{key}" });

        var file = db.File;
        int tdef = TdefOf(file, "Keyed");
        var block = JetPages.IndexBlocks(file, tdef).Single();
        Assert.Empty(JetPages.RuleBreaks(file, block.Root, tdef));
        Assert.Equal(5000, JetPages.LeafEntries(file, block.Root).Count);
        var tree = JetPages.TreePages(file, block.Root);
        Assert.True(tree.Count > 10, $"{tree.Count} pages");
        Assert.True(tree.Max(p => p.Level) >= 1);
        Assert.Subset(UsageMap.GetOwnedPages(file.ReadPage(block.UmapPage), block.UmapRow, file.Format, file).ToHashSet(),
                      tree.Select(p => p.Number).ToHashSet());

        var cursor = db.GetTable("Keyed").NewIndexCursor();
        foreach (int key in keys.Where((_, i) => i % 50 == 0))
            Assert.Equal($"v{key}", cursor.FindRowByPrimaryKey(key)?["Txt"]);
    }

    [Fact]
    public void A_two_column_key_has_a_flag_for_each_column()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Pair", new[]
        {
            new ColumnBuilder("A", DataType.Long).Build(),
            new ColumnBuilder("B", DataType.Text).MaxLength(10).Build(),
        }, new[] { "A", "B" });
        table.Insert(new Row { ["A"] = 5, ["B"] = "b" });

        var file = db.File;
        var entry = JetPages.LeafEntries(file, JetPages.IndexBlocks(file, TdefOf(file, "Pair")).Single().Root).Single();
        Assert.Equal("7F80000005" + "7F" + Convert.ToHexString(GeneralLegacyIndexCodes.EncodeText("b", isAscending: true)), entry.Hex);
    }

    [Fact]
    public void A_GUID_key_is_written_as_Access_writes_one()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Guids", new[] { new ColumnBuilder("G", DataType.Guid).Build() }, primaryKey: "G");
        table.Insert(new Row { ["G"] = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10") });

        var file = db.File;
        var entry = JetPages.LeafEntries(file, JetPages.IndexBlocks(file, TdefOf(file, "Guids")).Single().Root).Single();
        // The GUID's bytes as its text shows them, in 8-byte segments each followed by a length byte.
        Assert.Equal("7F" + "0102030405060708" + "09" + "090A0B0C0D0E0F10" + "08", entry.Hex);
    }

    [Fact]
    public void Pages_the_engine_adds_leave_the_free_page_map()
    {
        string path = NewPath();
        using var db = Database.Create(path, JetVersion.Jet4);
        var file = db.File;
        int blankEnd = file.PageCount;

        var table = db.CreateTable("Grows", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
        }, primaryKey: "Id");
        for (int i = 0; i < 40; i++)
            table.Insert(new Row { ["Id"] = i, ["Notes"] = new string('n', 6000) });

        var (isReference, free, _) = JetPages.GlobalFreePages(file, file.PageCount);
        Assert.False(isReference);
        Assert.Empty(free.Where(p => p >= blankEnd));
    }

    [Fact]
    public void Past_its_inline_window_the_free_page_map_becomes_a_reference_map()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var file = db.File;
        int blankEnd = file.PageCount;
        var table = db.CreateTable("Big", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
        });
        for (int i = 0; file.PageCount < 700; i++)
            table.Insert(new Row { ["Id"] = i, ["Notes"] = new string((char)('a' + i % 26), 8000) });

        var (isReference, free, mapPages) = JetPages.GlobalFreePages(file, file.PageCount + 10);
        Assert.True(isReference);
        Assert.All(mapPages, m => Assert.Equal(JetFormat.PageTypeUsageMap, file.ReadPage(m)[0]));
        Assert.Empty(free.Where(p => p >= blankEnd && p < file.PageCount));
        Assert.Contains(file.PageCount, free);   // the next page Access would add is free
    }

    [Fact]
    public void A_tables_usage_maps_are_on_a_data_page_that_no_table_owns()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Mapped", OneColumn());

        var file = db.File;
        var (umapPage, _) = JetPages.OwnedPagesMap(file, TdefOf(file, "Mapped"));
        byte[] page = file.ReadPage(umapPage);
        Assert.Equal(JetFormat.PageTypeData, page[0]);
        Assert.Equal(0, ByteUtil.GetInt(page, 4));
    }

    [Fact]
    public void A_table_name_is_written_uncompressed_as_MSysObjects_keeps_names()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Plain Name", OneColumn());

        var file = db.File;
        var mine = JetPages.Rows(file, JetFormat.PageSystemCatalog).Single(r => (string?)Value(r.Values, "Name") == "Plain Name");
        byte[] row = JetPages.RowBytes(file, mine.Page, mine.Row);
        string hex = Convert.ToHexString(row);
        Assert.Contains(Convert.ToHexString(Encoding.Unicode.GetBytes("Plain Name")), hex);
        Assert.DoesNotContain("FFFE" + Convert.ToHexString(Encoding.ASCII.GetBytes("Plain")), hex);
    }

    [Fact]
    public void The_engines_text_and_memo_columns_allow_unicode_compression()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        var table = db.CreateTable("Texts", new[]
        {
            new ColumnBuilder("Id", DataType.Long).Build(),
            new ColumnBuilder("Txt", DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Notes", DataType.Memo).Build(),
        });
        table.Insert(new Row { ["Id"] = 1, ["Txt"] = "café", ["Notes"] = "naïve" });

        var file = db.File;
        byte[] tdef = file.ReadPage(TdefOf(file, "Texts"));
        int columnDefs = 63 + ByteUtil.GetInt(tdef, 51) * 12;
        var compressed = Enumerable.Range(0, 3).Select(i => (tdef[columnDefs + i * 25 + 16] & 0x01) != 0).ToArray();
        Assert.Equal(new[] { false, true, true }, compressed);
        var row = db.GetTable("Texts").ReadAllRows().Single();
        Assert.Equal("café", row["Txt"]);
        Assert.Equal("naïve", row["Notes"]);
    }

    [Fact]
    public void A_second_table_of_the_same_name_is_refused()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);
        db.CreateTable("Once", OneColumn());

        Assert.Throws<InvalidOperationException>(() => db.CreateTable("ONCE", OneColumn()));
        Assert.Single(db.ListTables());
    }

    [Fact]
    public void A_primary_key_on_a_column_whose_keys_are_not_written_is_refused()
    {
        using var db = Database.Create(NewPath(), JetVersion.Jet4);

        Assert.Throws<NotSupportedException>(() =>
            db.CreateTable("Doubles", new[] { new ColumnBuilder("D", DataType.Double).Build() }, primaryKey: "D"));
        Assert.Empty(db.ListTables());
    }
}
