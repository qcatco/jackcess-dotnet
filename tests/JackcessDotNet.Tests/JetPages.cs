using JackcessDotNet.Util;

namespace JackcessDotNet.Tests;

/// <summary>
/// Reads a Jet4 file's pages the way Access lays them out, independently of the
/// engine's own readers, for tests that check what the engine wrote: index pages
/// and the rules Access keeps for them, the index blocks of a TDEF, a table's rows
/// with their row pointers, and the database's global usage map.
/// </summary>
internal static class JetPages
{
    // Jet4 index page header.
    private const int OffsetTdef = 4, OffsetPrev = 12, OffsetNext = 16, OffsetTail = 20, OffsetPrefix = 24, OffsetLevel = 26;
    private const int OffsetMask = 27, MaskSize = 453, EntriesStart = OffsetMask + MaskSize;

    public sealed record Entry(byte[] Key, int RowPage, int Row, int SubPage)
    {
        public string Hex => Convert.ToHexString(Key);
    }

    public sealed record IndexPage(int Number, bool IsLeaf, int Tdef, int Prev, int Next, int Tail, int Level, List<Entry> Entries);

    public static IndexPage ReadIndexPage(PageFile file, int number)
    {
        byte[] p = file.ReadPage(number);
        if (p[0] != 0x03 && p[0] != 0x04)
            throw new InvalidDataException($"Page {number} is not an index page (0x{p[0]:X2}).");
        bool leaf = p[0] == 0x04;
        int prefixLen = ByteUtil.GetUShort(p, OffsetPrefix);
        int trailer = leaf ? 4 : 8;
        var entries = new List<Entry>();
        byte[]? prefix = null;
        int start = 0;
        for (int i = 0; i < MaskSize; i++)
        {
            byte b = p[OffsetMask + i];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << bit)) == 0) continue;
                int end = i * 8 + bit;
                byte[] stored = p.Skip(EntriesStart + start).Take(end - start).ToArray();
                byte[] full = entries.Count > 0 && prefix is not null ? prefix.Concat(stored).ToArray() : stored;
                if (entries.Count == 0 && prefixLen > 0) prefix = stored.Take(prefixLen).ToArray();
                int k = full.Length - trailer;
                int sub = leaf ? 0 : (full[k + 4] << 24) | (full[k + 5] << 16) | (full[k + 6] << 8) | full[k + 7];
                entries.Add(new Entry(full.Take(k).ToArray(), (full[k] << 16) | (full[k + 1] << 8) | full[k + 2], full[k + 3], sub));
                start = end;
            }
        }
        return new IndexPage(number, leaf, ByteUtil.GetInt(p, OffsetTdef), ByteUtil.GetInt(p, OffsetPrev),
                             ByteUtil.GetInt(p, OffsetNext), ByteUtil.GetInt(p, OffsetTail), p[OffsetLevel], entries);
    }

    /// <summary>Every leaf entry of the tree, from the leftmost leaf along the leaf chain.</summary>
    public static List<Entry> LeafEntries(PageFile file, int root)
    {
        var page = ReadIndexPage(file, root);
        while (!page.IsLeaf)
            page = ReadIndexPage(file, page.Entries.Count > 0 ? page.Entries[0].SubPage : page.Tail);
        var all = new List<Entry>();
        while (true)
        {
            all.AddRange(page.Entries);
            if (page.Next <= 0) return all;
            page = ReadIndexPage(file, page.Next);
        }
    }

    /// <summary>Every page of the tree rooted at <paramref name="root"/>.</summary>
    public static List<IndexPage> TreePages(PageFile file, int root)
    {
        var pages = new List<IndexPage>();
        var todo = new Queue<int>();
        todo.Enqueue(root);
        while (todo.Count > 0)
        {
            var page = ReadIndexPage(file, todo.Dequeue());
            pages.Add(page);
            if (page.IsLeaf) continue;
            foreach (var e in page.Entries) todo.Enqueue(e.SubPage);
            if (page.Tail > 0) todo.Enqueue(page.Tail);
        }
        return pages;
    }

    /// <summary>
    /// What breaks Access's rules in the tree rooted at <paramref name="root"/>: every
    /// page names <paramref name="tdefPage"/>; a node's entries are copies of the last
    /// entry under each child but its last, which its tail leads to; entries are sorted
    /// by key, then row; each level is linked through prev/next with 0 for no page;
    /// a page's level is its height above the leaves.
    /// </summary>
    public static List<string> RuleBreaks(PageFile file, int root, int tdefPage)
    {
        var problems = new List<string>();
        int Height(IndexPage page) => page.IsLeaf ? 0 : 1 + Height(ReadIndexPage(file, page.Tail > 0 ? page.Tail : page.Entries[^1].SubPage));
        Entry LastUnder(IndexPage page) => page.IsLeaf ? page.Entries[^1]
            : LastUnder(ReadIndexPage(file, page.Tail > 0 ? page.Tail : page.Entries[^1].SubPage));

        var levels = new Dictionary<int, List<IndexPage>>();
        foreach (var page in TreePages(file, root))
        {
            if (page.Tdef != tdefPage) problems.Add($"page {page.Number} names TDEF {page.Tdef}, not {tdefPage}");
            if (page.Prev < 0 || page.Next < 0 || page.Tail < 0) problems.Add($"page {page.Number} has a link of -1");
            if (page.IsLeaf && page.Tail != 0) problems.Add($"leaf {page.Number} has a tail");
            int height = Height(page);
            if (page.Level != height) problems.Add($"page {page.Number} says level {page.Level}, is {height}");
            for (int i = 1; i < page.Entries.Count; i++)
                if (Compare(page.Entries[i - 1], page.Entries[i]) >= 0) problems.Add($"page {page.Number} entry {i} is out of order");
            if (!page.IsLeaf)
            {
                if (page.Tail <= 0) problems.Add($"node {page.Number} has no tail");
                foreach (var e in page.Entries)
                {
                    var last = LastUnder(ReadIndexPage(file, e.SubPage));
                    if (Compare(last, e) != 0) problems.Add($"node {page.Number}: its entry for {e.SubPage} is not the last entry under it");
                }
                if (page.Tail > 0 && page.Entries.Count > 0)
                {
                    var tailFirst = FirstUnder(file, ReadIndexPage(file, page.Tail));
                    if (tailFirst is not null && Compare(tailFirst, page.Entries[^1]) <= 0)
                        problems.Add($"node {page.Number}: its tail holds an entry before its last entry");
                }
            }
            (levels.TryGetValue(height, out var list) ? list : levels[height] = new List<IndexPage>()).Add(page);
        }

        // Each level is one chain, in key order.
        foreach (var (height, pages) in levels)
        {
            var byNumber = pages.ToDictionary(p => p.Number);
            var heads = pages.Where(p => p.Prev == 0).ToList();
            if (heads.Count != 1) { problems.Add($"level {height} has {heads.Count} first pages"); continue; }
            int seen = 0;
            Entry? previous = null;
            for (var page = heads[0]; ; page = byNumber[page.Next])
            {
                seen++;
                foreach (var e in page.Entries)
                {
                    if (previous is not null && height == 0 && Compare(previous, e) >= 0)
                        problems.Add($"level {height}: page {page.Number} is out of order");
                    previous = e;
                }
                if (page.Next == 0) break;
                if (!byNumber.TryGetValue(page.Next, out var next) || next.Prev != page.Number)
                {
                    problems.Add($"level {height}: page {page.Number}'s next does not lead back to it");
                    break;
                }
            }
            if (seen != pages.Count) problems.Add($"level {height}: its chain holds {seen} of {pages.Count} pages");
        }
        return problems;
    }

    private static Entry? FirstUnder(PageFile file, IndexPage page)
    {
        while (!page.IsLeaf)
            page = ReadIndexPage(file, page.Entries.Count > 0 ? page.Entries[0].SubPage : page.Tail);
        return page.Entries.FirstOrDefault();
    }

    public static int Compare(Entry a, Entry b)
    {
        int n = Math.Min(a.Key.Length, b.Key.Length);
        for (int i = 0; i < n; i++)
            if (a.Key[i] != b.Key[i]) return a.Key[i] - b.Key[i];
        if (a.Key.Length != b.Key.Length) return a.Key.Length - b.Key.Length;
        return a.RowPage != b.RowPage ? a.RowPage.CompareTo(b.RowPage) : a.Row.CompareTo(b.Row);
    }

    public sealed record IndexBlock(int Root, int UmapPage, int UmapRow);

    /// <summary>The real index blocks of the TDEF on <paramref name="tdefPage"/>, in block order.</summary>
    public static List<IndexBlock> IndexBlocks(PageFile file, int tdefPage)
    {
        byte[] p = file.ReadPage(tdefPage);
        int numCols = ByteUtil.GetShort(p, 45);
        int numReal = ByteUtil.GetInt(p, 51);
        int pos = 63 + numReal * 12 + numCols * 25;
        for (int i = 0; i < numCols; i++) pos += 2 + ByteUtil.GetShort(p, pos);
        var blocks = new List<IndexBlock>();
        for (int i = 0; i < numReal; i++, pos += 52)
            blocks.Add(new IndexBlock(ByteUtil.GetInt(p, pos + 4 + 30 + 4), ByteUtil.Get3ByteInt(p, pos + 4 + 30 + 1), p[pos + 4 + 30]));
        return blocks;
    }

    /// <summary>The table's owned-pages usage map reference in its TDEF: (page, row).</summary>
    public static (int Page, int Row) OwnedPagesMap(PageFile file, int tdefPage)
    {
        byte[] p = file.ReadPage(tdefPage);
        return (ByteUtil.Get3ByteInt(p, 56), p[55]);
    }

    /// <summary>Every row of the table on <paramref name="tdefPage"/>, decoded, with its page and row.</summary>
    public static List<(int Page, int Row, Row Values)> Rows(PageFile file, int tdefPage)
    {
        var info = TdefReader.Read(file.ReadPage(tdefPage), file.Format);
        var decoder = new RowDecoder(file.Format, info.Columns);
        var (umapPage, umapRow) = OwnedPagesMap(file, tdefPage);
        var result = new List<(int, int, Row)>();
        foreach (int pageNumber in UsageMap.GetOwnedPages(file.ReadPage(umapPage), umapRow, file.Format, file))
        {
            byte[] page = file.ReadPage(pageNumber);
            int count = ByteUtil.GetShort(page, file.Format.OffsetDataNumRows);
            for (int r = 0; r < count; r++)
            {
                int slot = ByteUtil.GetUShort(page, file.Format.OffsetDataRowTable + r * 2);
                if ((slot & 0xC000) != 0) continue;
                int start = slot & 0x1FFF;
                int end = r == 0 ? page.Length : ByteUtil.GetUShort(page, file.Format.OffsetDataRowTable + (r - 1) * 2) & 0x1FFF;
                byte[] bytes = page.Skip(start).Take(end - start).ToArray();
                var row = new Row();
                foreach (var column in info.Columns)
                    if (decoder.Decode(bytes, column) is { } value) row[column.Name] = value;
                result.Add((pageNumber, r, row));
            }
        }
        return result;
    }

    /// <summary>The raw bytes of a row.</summary>
    public static byte[] RowBytes(PageFile file, int pageNumber, int row)
    {
        byte[] page = file.ReadPage(pageNumber);
        int start = ByteUtil.GetUShort(page, file.Format.OffsetDataRowTable + row * 2) & 0x1FFF;
        int end = row == 0 ? page.Length : ByteUtil.GetUShort(page, file.Format.OffsetDataRowTable + (row - 1) * 2) & 0x1FFF;
        return page.Skip(start).Take(end - start).ToArray();
    }

    /// <summary>The pages the global usage map (page 1, row 0) calls free, up to <paramref name="limit"/>; and whether it is a reference map.</summary>
    public static (bool IsReference, List<int> Free, List<int> MapPages) GlobalFreePages(PageFile file, int limit)
    {
        byte[] p = file.ReadPage(1);
        int start = ByteUtil.GetUShort(p, file.Format.OffsetDataRowTable) & 0x1FFF;
        int end = p.Length;
        var free = new List<int>();
        if (p[start] == 0x00)
        {
            int first = ByteUtil.GetInt(p, start + 1);
            for (int i = 0; i < (end - start - 5) * 8; i++)
                if ((p[start + 5 + i / 8] & (1 << (i % 8))) != 0 && first + i < limit) free.Add(first + i);
            return (false, free, new List<int>());
        }
        var mapPages = new List<int>();
        int perMapPage = (file.Format.PageSize - 4) * 8;
        for (int i = 0; start + 1 + i * 4 + 4 <= end; i++)
        {
            int mapPage = ByteUtil.GetInt(p, start + 1 + i * 4);
            if (mapPage <= 0) continue;
            mapPages.Add(mapPage);
            byte[] m = file.ReadPage(mapPage);
            for (int bit = 0; bit < perMapPage && i * perMapPage + bit < limit; bit++)
                if ((m[4 + bit / 8] & (1 << (bit % 8))) != 0) free.Add(i * perMapPage + bit);
        }
        return (true, free, mapPages);
    }
}
