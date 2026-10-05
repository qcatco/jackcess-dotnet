using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Adds entries to an index's B-tree the way Access writes one, so Access reads
/// the tree and can go on adding to it: the primary key of a table this library
/// creates, and MSysObjects' and MSysACEs' indexes when it creates a table.
///
/// Access's rules, as its own pages show them:
///   • A leaf's entries are sorted by key, then by row pointer; each is the key
///     followed by the row's page (3 bytes, big-endian) and row (1 byte).
///   • A node holds, for each child but its last, a copy of the last entry under
///     that child followed by the child's page (4 bytes, big-endian). The last
///     child has no entry: the node's tail pointer leads to it.
///   • Every level is a list linked through prev/next; 0 is "no page".
///   • A page's header names its table's TDEF page and (Jet4) its height above
///     the leaves.
///   • Every page of an index is in the index's used-pages usage map.
///
/// A page stores the bytes all its keys begin with once, as Access does: the first
/// entry is written in full, the rest without that prefix, whose length the page
/// header holds (Jackcess takes it the same way, from the first and last keys).
/// A full Access page holds more entries than would fit written out in full, so
/// every page is written that way. A page that does not fit, after any change,
/// splits - in two, or, where a key sharing nothing with its neighbours undoes the
/// packing, into as many pieces as halving takes - and its parent gains an entry
/// for each new piece; a parent that overflows splits the same way. The root keeps
/// its page, as Jackcess's does: when it splits, its entries move to new pages
/// beneath it and it becomes the node above them, and a root left with one child
/// takes that child's entries back, as often as that leaves it with one child. So
/// the TDEF's root, and every copy of it a reader or writer holds, stays right.
///
/// Pages written by earlier versions of this library (an entry for every child,
/// each with row pointer 0, and no tail; 0xFFFFFFFF for "no page"; no TDEF named)
/// are read either way and brought to these rules, children and all, when the
/// tree is changed.
/// </summary>
public sealed class IndexWriter
{
    private const int LeafTrailerLen = 4;   // 3-byte BE page + 1-byte row
    private const int NodeTrailerLen = 8;   // leaf trailer + 4-byte BE child page
    private const int OffsetTdefPage = 4;
    private const int OldNoPage      = unchecked((int)0xFFFFFFFF);

    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;
    private readonly JetFormat     _format;

    public IndexWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
        _format    = file.Format;
    }

    // ── Primary keys ─────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh, empty leaf page and returns its page number.</summary>
    public int CreateEmptyLeafPage() => CreateEmptyLeafPage(tdefPage: 0);

    internal int CreateEmptyLeafPage(int tdefPage)
    {
        var page = new IndexPage(_allocator.AllocatePage(), isLeaf: true, raw: null) { Tdef = tdefPage };
        Write(page);
        return page.Number;
    }

    public int CreatePrimaryKeyIndex(TableDefinition table, string indexName)
        => CreateEmptyLeafPage(table.TdefPageNumber);

    /// <summary>Composite-key companion of <see cref="CreatePrimaryKeyIndex(TableDefinition, string)"/>.</summary>
    public int CreatePrimaryKeyIndex(TableDefinition table, IReadOnlyList<string> pkColumns)
        => CreateEmptyLeafPage(table.TdefPageNumber);

    public int? FindRowByPrimaryKey(TableDefinition table, object primaryKeyValue)
    {
        foreach (int rowPtr in EnumerateRowPointersForKey(table, primaryKeyValue))
            return rowPtr;
        return null;
    }

    /// <summary>Composite-key variant of <see cref="FindRowByPrimaryKey(TableDefinition, object)"/>.</summary>
    public int? FindRowByPrimaryKey(TableDefinition table, IReadOnlyList<object?> values)
    {
        if (table.PrimaryKeyIndexPage == 0) return null;
        foreach (int rowPtr in RowPointersForKey(table.PrimaryKeyIndexPage, PrimaryKeyBytes(table, values)))
            return rowPtr;
        return null;
    }

    /// <summary>
    /// Every row pointer the primary key holds under <paramref name="primaryKeyValue"/>,
    /// including those an update left behind (callers check the row).
    /// </summary>
    public IEnumerable<int> EnumerateRowPointersForKey(TableDefinition table, object primaryKeyValue)
    {
        if (table.PrimaryKeyIndexPage == 0) return Array.Empty<int>();
        return RowPointersForKey(table.PrimaryKeyIndexPage, PrimaryKeyBytes(table, new[] { primaryKeyValue }));
    }

    /// <summary>
    /// Adds a (key, row) entry to the table's primary key. The root stays on its page
    /// (see the class summary), so <see cref="TableDefinition.PrimaryKeyIndexPage"/> and
    /// the TDEF stay right, for this table object and any other.
    /// </summary>
    public void InsertPrimaryKey(TableDefinition table, object primaryKeyValue, int rowPointer)
        => InsertPrimaryKey(table, new[] { primaryKeyValue }, rowPointer);

    /// <summary>Composite-key variant of <see cref="InsertPrimaryKey(TableDefinition, object, int)"/>.</summary>
    public void InsertPrimaryKey(TableDefinition table, IReadOnlyList<object?> values, int rowPointer)
    {
        if (table.PrimaryKeyIndexPage == 0)
            throw new InvalidOperationException(
                "Table has no primary key index page. " +
                "Specify a primary key column name when calling Database.CreateTable.");

        var target = new Target(table.TdefPageNumber, table.PrimaryKeyIndexPage,
                                table.PrimaryKeyIndexUmapPage, table.PrimaryKeyIndexUmapRow);
        Insert(target, PrimaryKeyBytes(table, values), rowPointer);
    }

    /// <summary>
    /// Takes the entry for <paramref name="values"/> at <paramref name="rowPointer"/> out of the table's primary
    /// key: a row deleted, or moved by an update.
    /// </summary>
    internal void RemovePrimaryKey(TableDefinition table, IReadOnlyList<object?> values, int rowPointer)
    {
        if (table.PrimaryKeyIndexPage == 0) return;
        var target = new Target(table.TdefPageNumber, table.PrimaryKeyIndexPage,
                                table.PrimaryKeyIndexUmapPage, table.PrimaryKeyIndexUmapRow);
        Remove(target, PrimaryKeyBytes(table, values), rowPointer);
    }

    /// <summary>
    /// The primary key's columns: from the index on disk when the table was read,
    /// or from its definition (ascending) when this library made it.
    /// </summary>
    internal static IReadOnlyList<IndexColumn> PrimaryKeyColumns(TableDefinition table)
    {
        var pk = table.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        if (pk is not null && pk.Columns.Count > 0) return pk.Columns;
        return table.EffectivePrimaryKeyColumns
            .Select(name => new IndexColumn(
                table.Columns.First(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)), 0x01))
            .ToList();
    }

    private static byte[] PrimaryKeyBytes(TableDefinition table, IReadOnlyList<object?> values)
    {
        var columns = PrimaryKeyColumns(table);
        foreach (var value in values)
            if (value is null)
                throw new NotSupportedException("A primary key value cannot be null.");
        return IndexKeys.Encode(columns, values);
    }

    // ── Any index ────────────────────────────────────────────────────────────

    /// <summary>An index to change: its table's TDEF page, its root page (which never moves), and its used-pages usage map.</summary>
    internal sealed class Target
    {
        public Target(int tdefPage, int rootPage, int umapPage, int umapRow)
        {
            TdefPage = tdefPage;
            RootPage = rootPage;
            UmapPage = umapPage;
            UmapRow  = umapRow;
        }

        public static Target For(int tdefPage, Index index)
            => new(tdefPage, index.RootPageNumber, index.UsedPagesUmapPage, index.UsedPagesUmapRow);

        public int TdefPage { get; }
        public int RootPage { get; }
        public int UmapPage { get; }
        public int UmapRow  { get; }
    }

    /// <summary>
    /// Adds the entry (<paramref name="key"/>, <paramref name="rowPointer"/>), splitting
    /// pages up the tree as they fill.
    /// </summary>
    internal void Insert(Target target, byte[] key, int rowPointer)
    {
        var entry = new Entry(key, rowPointer, 0);
        var path  = new List<(IndexPage Node, int Slot)>();   // Slot -1: the node's tail

        var page = ReadFor(target, target.RootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target);
            int slot = FirstAtLeast(page.Entries, entry);
            path.Add((page, slot));
            page = ReadFor(target, slot >= 0 ? page.Entries[slot].SubPage : page.Tail);
        }
        InsertSorted(page.Entries, entry);
        page.Level = 0;
        WriteUp(path, path.Count, page, target);
    }

    /// <summary>
    /// Writes <paramref name="child"/>, the page at <paramref name="depth"/> on <paramref name="path"/> (whose
    /// parent is <c>path[depth - 1]</c>): while a page does not fit, it splits and its parent gains the entry, up to
    /// the root, which splits in place.
    /// </summary>
    private void WriteUp(List<(IndexPage Node, int Slot)> path, int depth, IndexPage child, Target target)
    {
        for (; ; depth--)
        {
            if (Fits(child))
            {
                Write(child);
                return;
            }

            if (depth == 0)
            {
                SplitRoot(child, target);
                return;
            }

            var (pieces, lasts) = Split(child, target, keepNumber: true);
            var added = lasts.Select((last, i) => new Entry(last.Key, last.RowPtr, pieces[i].Number)).ToList();

            var (parent, parentSlot) = path[depth - 1];
            if (parentSlot >= 0)
            {
                // The split page's entry now names its last piece, which holds its last row;
                // each piece before it gets an entry of its own.
                var old = parent.Entries[parentSlot];
                parent.Entries[parentSlot] = new Entry(old.Key, old.RowPtr, pieces[^1].Number);
                parent.Entries.InsertRange(parentSlot, added);
            }
            else
            {
                // The split page was the tail: the pieces before its last get entries; its last is the tail.
                parent.Entries.AddRange(added);
                parent.Tail = pieces[^1].Number;
            }
            parent.Level = child.Level + 1;
            child = parent;
        }
    }

    /// <summary>
    /// Takes the entry (<paramref name="key"/>, <paramref name="rowPointer"/>) out of the tree, as Access and
    /// Jackcess do: a page left empty leaves the tree and its level's chain (a tail leaving its node, the node's
    /// last entry's child becomes the tail), an entry copying the last entry under a page follows it when that
    /// changes, and a root left with one child gives way to it. An entry the tree does not hold is no change.
    /// </summary>
    internal void Remove(Target target, byte[] key, int rowPointer)
    {
        var entry = new Entry(key, rowPointer, 0);
        var path  = new List<(IndexPage Node, int Slot)>();   // Slot -1: the node's tail

        var page = ReadFor(target, target.RootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target);
            int slot = FirstAtLeast(page.Entries, entry);
            path.Add((page, slot));
            page = ReadFor(target, slot >= 0 ? page.Entries[slot].SubPage : page.Tail);
        }
        int at = page.Entries.FindIndex(e => Compare(e, entry) == 0);
        if (at < 0) return;
        page.Entries.RemoveAt(at);
        page.Level = 0;

        // The last entry under the page, when the removal changed it.
        Entry? newLast = at == page.Entries.Count && page.Entries.Count > 0 ? page.Entries[^1] : null;

        // An empty page leaves the tree; its parent loses the entry for it, or its tail.
        var child = page;
        int depth = path.Count;
        while (depth > 0 && child.Entries.Count == 0 && (child.IsLeaf || child.Tail <= 0))
        {
            Drop(child, target);
            var (parent, slot) = path[depth - 1];
            if (slot >= 0)
            {
                parent.Entries.RemoveAt(slot);
                newLast = null;   // the parent's tail still holds what is last under it
            }
            else if (parent.Entries.Count > 0)
            {
                var last = parent.Entries[^1];
                parent.Entries.RemoveAt(parent.Entries.Count - 1);
                parent.Tail = last.SubPage;
                newLast = last;
            }
            else
            {
                parent.Tail = 0;
                newLast = null;
            }
            child = parent;
            depth--;
        }

        if (depth == 0 && !child.IsLeaf && child.Entries.Count == 0)
        {
            CollapseRoot(child, target);
            return;
        }
        WriteUp(path, depth, child, target);

        // The first ancestor naming the page in an entry copies its new last entry, which can
        // make that ancestor's page too full: it splits as an insert's would.
        if (newLast is { } changed)
        {
            for (int d = depth - 1; d >= 0; d--)
            {
                var (parent, slot) = path[d];
                if (slot < 0) continue;
                parent.Entries[slot] = new Entry(changed.Key, changed.RowPtr, parent.Entries[slot].SubPage);
                WriteUp(path, d, parent, target);
                break;
            }
        }
    }

    /// <summary>
    /// A root with one child takes that child's entries back, on its own page, for as
    /// long as that leaves it with one child; a root with none is an empty leaf again.
    /// </summary>
    private void CollapseRoot(IndexPage root, Target target)
    {
        while (!root.IsLeaf && root.Entries.Count == 0)
        {
            if (root.Tail <= 0)
            {
                Write(new IndexPage(root.Number, isLeaf: true, root.Raw) { Tdef = target.TdefPage });
                return;
            }
            var only = Read(root.Tail);
            var taken = new IndexPage(root.Number, only.IsLeaf, root.Raw)
            {
                Tdef  = target.TdefPage,
                Level = only.Level,
                Tail  = only.Tail,
            };
            taken.Entries.AddRange(only.Entries);
            if (!Fits(taken)) break;   // a child Access packed tighter than these pages are written
            if (target.UmapPage > 0)
                UsageMap.RemovePage(_file, target.UmapPage, target.UmapRow, only.Number);
            root = taken;
        }
        Write(root);
    }

    // A page leaving the tree: out of its level's chain and its index's usage map.
    private void Drop(IndexPage page, Target target)
    {
        if (page.Prev > 0) PatchLink(page.Prev, _format.OffsetNextIndexPage, page.Next);
        if (page.Next > 0) PatchLink(page.Next, _format.OffsetPrevIndexPage, page.Prev);
        if (target.UmapPage > 0)
            UsageMap.RemovePage(_file, target.UmapPage, target.UmapRow, page.Number);
    }

    /// <summary>
    /// The row pointers of every entry whose key is <paramref name="key"/>, in order,
    /// in the tree rooted at <paramref name="rootPage"/>.
    /// </summary>
    internal IEnumerable<int> RowPointersForKey(int rootPage, byte[] key)
    {
        var first = new Entry(key, 0, 0);
        var page  = Read(rootPage);
        while (!page.IsLeaf)
        {
            UseTail(page, target: null);
            int slot  = FirstAtLeast(page.Entries, first);
            int child = slot >= 0 ? page.Entries[slot].SubPage : page.Tail;
            if (child <= 0) yield break;
            page = Read(child);
        }

        while (true)
        {
            foreach (var e in page.Entries)
            {
                int cmp = CompareBytes(e.Key, key);
                if (cmp == 0) yield return e.RowPtr;
                else if (cmp > 0) yield break;
            }
            if (page.Next <= 0) yield break;
            page = Read(page.Next);
        }
    }

    // ── Splitting ────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits <paramref name="page"/>, which does not fit, into pieces that each do:
    /// it halves, and a half that still does not fit halves again, as a page holding
    /// keys that share a long prefix and one that shares none can need. With
    /// <paramref name="keepNumber"/> the first piece keeps the page's number and the
    /// pieces take its place in its level's chain; otherwise they are all new pages,
    /// chained to one another. Writes them and returns them in order with, for each
    /// but the last, the last entry under it, which its parent holds a copy of (the
    /// last piece's is the page's own).
    /// </summary>
    private (List<IndexPage> Pieces, List<Entry> Lasts) Split(IndexPage page, Target target, bool keepNumber)
    {
        var contents = new List<(List<Entry> Entries, int Tail)>();
        var lasts    = new List<Entry>();
        Halve(page.IsLeaf, page.Entries, page.Tail, contents, lasts);

        var pieces = new List<IndexPage>();
        for (int i = 0; i < contents.Count; i++)
        {
            var piece = i == 0 && keepNumber
                ? new IndexPage(page.Number, page.IsLeaf, page.Raw)
                : new IndexPage(_allocator.AllocatePage(), page.IsLeaf, raw: null);
            piece.Tdef  = target.TdefPage;
            piece.Level = page.Level;
            piece.Tail  = contents[i].Tail;
            piece.Entries.AddRange(contents[i].Entries);
            pieces.Add(piece);
        }
        for (int i = 0; i < pieces.Count; i++)
        {
            pieces[i].Prev = i > 0 ? pieces[i - 1].Number : keepNumber ? page.Prev : 0;
            pieces[i].Next = i < pieces.Count - 1 ? pieces[i + 1].Number : keepNumber ? page.Next : 0;
        }
        if (keepNumber && page.Next > 0)
            PatchLink(page.Next, _format.OffsetPrevIndexPage, pieces[^1].Number);

        foreach (var piece in pieces)
            Write(piece);
        foreach (var piece in pieces.Skip(keepNumber ? 1 : 0))
            Own(target, piece.Number);
        return (pieces, lasts);
    }

    // Halves a page's content until each part fits, adding the parts and, between them, the
    // last entry under each part but the last. A node's middle entry goes up between its halves:
    // its child becomes the left half's tail.
    private void Halve(bool isLeaf, List<Entry> entries, int tail, List<(List<Entry>, int)> parts, List<Entry> lasts)
    {
        if (Fits(isLeaf, entries))
        {
            parts.Add((entries, tail));
            return;
        }
        int mid = entries.Count / 2;
        if (isLeaf)
        {
            if (entries.Count < 2)
                throw new InvalidDataException($"An index entry of {entries[0].Key.Length + LeafTrailerLen} bytes does not fit a page.");
            Halve(true, entries.GetRange(0, mid), 0, parts, lasts);
            lasts.Add(entries[mid - 1]);
            Halve(true, entries.GetRange(mid, entries.Count - mid), 0, parts, lasts);
        }
        else
        {
            if (entries.Count < 3)
                throw new InvalidDataException($"An index node of {entries.Count} entries does not fit a page.");
            var middle = entries[mid];
            Halve(false, entries.GetRange(0, mid), middle.SubPage, parts, lasts);
            lasts.Add(middle);
            Halve(false, entries.GetRange(mid + 1, entries.Count - mid - 1), tail, parts, lasts);
        }
    }

    /// <summary>
    /// Splits the root without moving it, as Jackcess does: its content goes to new
    /// pages beneath it, as many as fit it, chained as their level, and it becomes the
    /// node above them - splitting again, the same way, if that node does not fit.
    /// </summary>
    private void SplitRoot(IndexPage root, Target target)
    {
        var (pieces, lasts) = Split(root, target, keepNumber: false);
        var node = new IndexPage(root.Number, isLeaf: false, root.Raw)
        {
            Tdef  = target.TdefPage,
            Level = root.Level + 1,
            Tail  = pieces[^1].Number,
        };
        node.Entries.AddRange(lasts.Select((last, i) => new Entry(last.Key, last.RowPtr, pieces[i].Number)));
        if (Fits(node)) Write(node);
        else SplitRoot(node, target);
    }

    /// <summary>
    /// A node written by an earlier version of this library has an entry for every
    /// child, each with row pointer 0, and no tail: its last child becomes the tail.
    /// When the tree is being changed (a <paramref name="target"/>), each entry also
    /// takes the row of the last entry under its child, so entries compare as Access's
    /// do; its children are written back naming the TDEF, with 0 for no page; and the
    /// node is written back with its level. A search for a key's first entry (row 0)
    /// goes to the same child either way, so a search reads no more.
    /// </summary>
    private void UseTail(IndexPage node, Target? target)
    {
        if (node.Tail > 0 || node.Entries.Count == 0) return;
        node.Tail = node.Entries[^1].SubPage;
        node.Entries.RemoveAt(node.Entries.Count - 1);
        if (target is null) return;
        for (int i = 0; i < node.Entries.Count; i++)
        {
            if (LastEntryUnder(node.Entries[i].SubPage) is { } last)
                node.Entries[i] = new Entry(last.Key, last.RowPtr, node.Entries[i].SubPage);
        }
        foreach (int child in node.Entries.Select(e => e.SubPage).Append(node.Tail))
            Write(ReadFor(target, child));
        node.Level = HeightOf(node);
        Write(node);
    }

    // The last entry in the subtree under a page: down its tails (or last entries) to a leaf.
    private Entry? LastEntryUnder(int pageNumber)
    {
        var page = Read(pageNumber);
        while (!page.IsLeaf)
        {
            int next = page.Tail > 0 ? page.Tail : page.Entries.Count > 0 ? page.Entries[^1].SubPage : 0;
            if (next <= 0) return null;
            page = Read(next);
        }
        return page.Entries.Count > 0 ? page.Entries[^1] : null;
    }

    // A page's height above the leaves, found by walking down its last children.
    private int HeightOf(IndexPage page)
    {
        int height = 0;
        while (!page.IsLeaf)
        {
            int next = page.Tail > 0 ? page.Tail : page.Entries.Count > 0 ? page.Entries[^1].SubPage : 0;
            if (next <= 0) break;
            page = Read(next);
            height++;
        }
        return height;
    }

    private void Own(Target target, int pageNumber)
    {
        if (target.UmapPage > 0)
            UsageMap.AddPage(_file, _allocator, target.UmapPage, target.UmapRow, pageNumber);
    }

    private void PatchLink(int pageNumber, int offset, int value)
    {
        byte[] raw = _file.ReadPage(pageNumber);
        ByteUtil.PutInt(raw, offset, value);
        _file.WritePage(pageNumber, raw);
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    private sealed class IndexPage
    {
        public IndexPage(int number, bool isLeaf, byte[]? raw)
        {
            Number = number;
            IsLeaf = isLeaf;
            Raw    = raw;
        }

        public int         Number  { get; }
        public bool        IsLeaf  { get; }
        public byte[]?     Raw     { get; }   // as read; null for a new page
        public int         Tdef    { get; set; }
        public int         Level   { get; set; }
        public int         Prev    { get; set; }
        public int         Next    { get; set; }
        public int         Tail    { get; set; }
        public List<Entry> Entries { get; } = new();
    }

    private readonly record struct Entry(byte[] Key, int RowPtr, int SubPage);

    private int EntriesStart => _format.OffsetIndexEntryMask + _format.SizeIndexEntryMask;

    // Jet4 keeps a page's height above the leaves in the byte after the shared-prefix length.
    private bool HasLevelByte => _format.OffsetIndexEntryMask - _format.OffsetIndexCompressedByteCount > 2;

    private IndexPage Read(int number)
    {
        byte[] raw = _file.ReadPage(number);
        if (raw[0] != JetFormat.PageTypeIndexLeaf && raw[0] != JetFormat.PageTypeIndexNode)
            throw new InvalidDataException($"Page {number} is not an index page (type 0x{raw[0]:X2}).");

        bool isLeaf = raw[0] == JetFormat.PageTypeIndexLeaf;
        var page = new IndexPage(number, isLeaf, raw)
        {
            Tdef  = ByteUtil.GetInt(raw, OffsetTdefPage),
            Level = HasLevelByte ? raw[_format.OffsetIndexCompressedByteCount + 2] : 0,
            Prev  = Link(ByteUtil.GetInt(raw, _format.OffsetPrevIndexPage)),
            Next  = Link(ByteUtil.GetInt(raw, _format.OffsetNextIndexPage)),
            Tail  = isLeaf ? 0 : Link(ByteUtil.GetInt(raw, _format.OffsetChildTailIndexPage)),
        };

        // Entries end where the mask has a bit set; the first is stored in full and
        // the rest without the prefix they share with it.
        int prefixLen  = ByteUtil.GetUShort(raw, _format.OffsetIndexCompressedByteCount);
        int trailerLen = isLeaf ? LeafTrailerLen : NodeTrailerLen;
        byte[]? prefix = null;
        int start = 0;
        for (int i = 0; i < _format.SizeIndexEntryMask; i++)
        {
            byte b = raw[_format.OffsetIndexEntryMask + i];
            if (b == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) == 0) continue;
                int end    = i * 8 + bit;
                int stored = end - start;
                byte[] full;
                if (page.Entries.Count == 0 || prefix is null)
                {
                    full = new byte[stored];
                    Array.Copy(raw, EntriesStart + start, full, 0, stored);
                    if (page.Entries.Count == 0 && prefixLen > 0)
                        prefix = full.Take(prefixLen).ToArray();
                }
                else
                {
                    full = new byte[prefix.Length + stored];
                    Array.Copy(prefix, full, prefix.Length);
                    Array.Copy(raw, EntriesStart + start, full, prefix.Length, stored);
                }
                start = end;

                if (full.Length < trailerLen)
                    throw new InvalidDataException($"Index page {number} has an entry of {full.Length} bytes.");
                int keyLen = full.Length - trailerLen;
                int rowPage = (full[keyLen] << 16) | (full[keyLen + 1] << 8) | full[keyLen + 2];
                int rowPtr  = RowPointer.Pack(rowPage, full[keyLen + 3]);
                int subPage = isLeaf ? 0
                    : (full[keyLen + 4] << 24) | (full[keyLen + 5] << 16) | (full[keyLen + 6] << 8) | full[keyLen + 7];
                page.Entries.Add(new Entry(full.Take(keyLen).ToArray(), rowPtr, subPage));
            }
        }
        return page;
    }

    private static int Link(int value) => value == OldNoPage ? 0 : value;

    // A page of the index being changed: it names the index's table, as Access's pages do.
    private IndexPage ReadFor(Target target, int number)
    {
        var page = Read(number);
        page.Tdef = target.TdefPage;
        return page;
    }

    /// <summary>
    /// The bytes every key of a page begins with: those its first and last keys
    /// share, the keys being in order. Stored once, in the first entry.
    /// </summary>
    private static int SharedPrefix(List<Entry> entries)
    {
        if (entries.Count < 2) return 0;
        byte[] first = entries[0].Key, last = entries[^1].Key;
        int n = Math.Min(first.Length, last.Length), shared = 0;
        while (shared < n && first[shared] == last[shared]) shared++;
        return shared;
    }

    private static int Size(bool isLeaf, List<Entry> entries) => entries.Sum(e => e.Key.Length)
        + entries.Count * (isLeaf ? LeafTrailerLen : NodeTrailerLen)
        - Math.Max(0, entries.Count - 1) * SharedPrefix(entries);

    private bool Fits(bool isLeaf, List<Entry> entries) => Size(isLeaf, entries) <= _format.PageSize - EntriesStart;

    private bool Fits(IndexPage page) => Fits(page.IsLeaf, page.Entries);

    private void Write(IndexPage p)
    {
        byte[] page = p.Raw ?? new byte[_format.PageSize];
        page[0] = p.IsLeaf ? JetFormat.PageTypeIndexLeaf : JetFormat.PageTypeIndexNode;
        page[1] = 0x01;
        ByteUtil.PutInt  (page, OffsetTdefPage,                    p.Tdef);
        ByteUtil.PutInt  (page, _format.OffsetPrevIndexPage,       p.Prev);
        ByteUtil.PutInt  (page, _format.OffsetNextIndexPage,       p.Next);
        ByteUtil.PutInt  (page, _format.OffsetChildTailIndexPage,  p.IsLeaf ? 0 : p.Tail);
        int shared = SharedPrefix(p.Entries);
        ByteUtil.PutShort(page, _format.OffsetIndexCompressedByteCount, (short)shared);
        if (HasLevelByte) page[_format.OffsetIndexCompressedByteCount + 2] = (byte)p.Level;
        if (!Fits(p))
            throw new InvalidOperationException(
                $"Index page {p.Number} holds {Size(p.IsLeaf, p.Entries)} bytes of entries; it has room for {_format.PageSize - EntriesStart}.");

        Array.Clear(page, _format.OffsetIndexEntryMask, _format.PageSize - _format.OffsetIndexEntryMask);
        int end = 0;
        for (int i = 0; i < p.Entries.Count; i++)
        {
            var e = p.Entries[i];
            int skip = i == 0 ? 0 : shared;   // the first entry holds the prefix the rest leave out
            int at = EntriesStart + end;
            Array.Copy(e.Key, skip, page, at, e.Key.Length - skip);
            at += e.Key.Length - skip;
            int rowPage = RowPointer.Page(e.RowPtr);
            page[at++] = (byte)(rowPage >> 16);
            page[at++] = (byte)(rowPage >> 8);
            page[at++] = (byte) rowPage;
            page[at++] = (byte)RowPointer.Row(e.RowPtr);
            if (!p.IsLeaf)
            {
                page[at++] = (byte)(e.SubPage >> 24);
                page[at++] = (byte)(e.SubPage >> 16);
                page[at++] = (byte)(e.SubPage >> 8);
                page[at++] = (byte) e.SubPage;
            }
            end = at - EntriesStart;
            page[_format.OffsetIndexEntryMask + end / 8] |= (byte)(1 << (end % 8));
        }
        ByteUtil.PutShort(page, 2, (short)(_format.PageSize - EntriesStart - end));
        _file.WritePage(p.Number, page);
    }

    // ── Ordering ─────────────────────────────────────────────────────────────

    // By key, then by row pointer (its page, then its row).
    private static int Compare(Entry a, Entry b)
    {
        int cmp = CompareBytes(a.Key, b.Key);
        return cmp != 0 ? cmp : ((uint)a.RowPtr).CompareTo((uint)b.RowPtr);
    }

    // The first entry at or after x; -1 when x is after them all.
    private static int FirstAtLeast(List<Entry> entries, Entry x)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Compare(entries[mid], x) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo < entries.Count ? lo : -1;
    }

    private static void InsertSorted(List<Entry> entries, Entry x)
    {
        int at = FirstAtLeast(entries, x);
        entries.Insert(at < 0 ? entries.Count : at, x);
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }
}
