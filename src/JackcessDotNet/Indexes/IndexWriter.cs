using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes B-tree index pages in real Access on-disk format and maintains the tree
/// shape across inserts.
///
/// Tree shape produced today:
///   • Root starts as a leaf page (type 0x04) for small indexes that fit on one page.
///   • On the first leaf overflow, root is promoted to a node page (type 0x03)
///     pointing at the original leaf (now left half) plus the new sibling leaf
///     (right half).
///   • Subsequent overflows split a leaf, update the corresponding node entry's
///     key to the new left-max, and add a new node entry for the right sibling.
///   • The depth is currently capped at 2 (root node + leaves). When the root
///     node itself fills, we throw — a 3-level tree needs recursive node splits,
///     deferred to a future slice.
///
/// Within a single page:
///   • Leaves carry entries sorted ascending by key bytes (so Access can binary-
///     search them).
///   • Node entries are also sorted; each entry's key is the LARGEST key in its
///     subtree (matches the descent convention <see cref="IndexReader"/> uses).
///
/// On a root change (initial leaf-to-node promotion), the index's
/// <c>rootPage</c> field inside the TDEF's index column block is patched so the
/// next <see cref="Database.Open"/> sees the new root.
/// </summary>
public sealed class IndexWriter
{
    private const int  NoPage         = unchecked((int)0xFFFFFFFF);
    private const byte AscStartFlag   = 0x7F;
    private const byte AscNullFlag    = 0x00;
    private const int  LeafTrailerLen = 4;   // 3-byte BE page + 1-byte row
    private const int  NodeTrailerLen = 8;   // leaf trailer + 4-byte BE sub-page

    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    public IndexWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh, empty leaf page and returns its page number.</summary>
    public int CreateEmptyLeafPage()
    {
        int pn = _allocator.AllocatePage();
        _file.WritePage(pn, BuildEmptyIndexPage(_file.Format, isLeaf: true));
        return pn;
    }

    public int CreatePrimaryKeyIndex(TableDefinition table, string indexName)
        => CreateEmptyLeafPage();

    /// <summary>
    /// Composite-PK companion to <see cref="CreatePrimaryKeyIndex(TableDefinition, string)"/>.
    /// The index layout is identical to a single-column PK — the difference is
    /// only in how key bytes get encoded on insertion.
    /// </summary>
    public int CreatePrimaryKeyIndex(TableDefinition table, IReadOnlyList<string> pkColumns)
        => CreateEmptyLeafPage();

    public int? FindRowByPrimaryKey(TableDefinition table, object primaryKeyValue)
    {
        foreach (int rowPtr in EnumerateRowPointersForKey(table, primaryKeyValue))
            return rowPtr;
        return null;
    }

    /// <summary>
    /// Composite-PK variant of <see cref="FindRowByPrimaryKey"/>. Encodes all
    /// values together and searches the tree once for the concatenated key.
    /// </summary>
    public int? FindRowByPrimaryKey(TableDefinition table, IReadOnlyList<object?> values)
    {
        byte[] key = EncodeCompositeKeyBytes(values);
        foreach (int rowPtr in EnumerateRowPointersForKeyBytes(table, key))
            return rowPtr;
        return null;
    }

    /// <summary>
    /// Walks the tree rooted at <see cref="TableDefinition.PrimaryKeyIndexPage"/>
    /// and yields rowPtrs for every entry whose key bytes equal the encoded form
    /// of <paramref name="primaryKeyValue"/>. Visits siblings via the leaf chain
    /// after the first matching leaf so duplicate keys (left by Update/Delete) are
    /// all reported.
    /// </summary>
    public IEnumerable<int> EnumerateRowPointersForKey(TableDefinition table, object primaryKeyValue)
        => EnumerateRowPointersForKeyBytes(table, EncodeKeyBytes(primaryKeyValue));

    /// <summary>
    /// Internal scan over the B-tree for the encoded key bytes. Shared between
    /// the single-column and composite-PK lookup paths.
    /// </summary>
    private IEnumerable<int> EnumerateRowPointersForKeyBytes(TableDefinition table, byte[] search)
    {
        if (table.PrimaryKeyIndexPage == 0) yield break;
        var format = _file.Format;

        // Descend from root to the leaf that should contain the key.
        int curLeaf = DescendToLeaf(table.PrimaryKeyIndexPage, search);
        while (curLeaf > 0 && curLeaf != NoPage)
        {
            byte[] page = _file.ReadPage(curLeaf);
            foreach (var entry in ReadEntries(page, format, isLeaf: true))
            {
                if (entry.KeyBytes.Length != search.Length) continue;
                bool match = true;
                for (int i = 0; i < search.Length; i++)
                    if (entry.KeyBytes[i] != search[i]) { match = false; break; }
                if (match) yield return entry.RowPtr;
            }
            int next = ByteUtil.GetInt(page, format.OffsetNextIndexPage);
            if (next == NoPage || next <= 0) break;   // 0 terminates, as Access writes it
            // Stop scanning siblings once the leaf's first key is past the search key
            // (entries are sorted ascending — no further matches possible).
            curLeaf = next;
            byte[] nextPage = _file.ReadPage(curLeaf);
            var firstEntry = ReadEntries(nextPage, format, isLeaf: true).FirstOrDefault();
            if (firstEntry.KeyBytes is null) break;
            if (CompareBytes(firstEntry.KeyBytes, search) > 0) break;
        }
    }

    /// <summary>
    /// Inserts a new (key, rowPtr) pair into the B-tree, splitting + promoting
    /// when necessary. May change <see cref="TableDefinition.PrimaryKeyIndexPage"/>
    /// if the root splits.
    /// </summary>
    public void InsertPrimaryKey(TableDefinition table, object primaryKeyValue, int rowPointer)
        => InsertPrimaryKeyBytes(table, EncodeKeyBytes(primaryKeyValue), rowPointer);

    /// <summary>
    /// Composite-PK variant. Encodes the multi-column key (single ascending
    /// flag prefix + per-column bytes) and inserts it into the same B-tree.
    /// </summary>
    public void InsertPrimaryKey(TableDefinition table, IReadOnlyList<object?> values, int rowPointer)
        => InsertPrimaryKeyBytes(table, EncodeCompositeKeyBytes(values), rowPointer);

    /// <summary>
    /// Inserts (key → rowPointer) into the B-tree rooted at <paramref name="rootPage"/>,
    /// for an index other than the table's primary key.
    /// <para>
    /// Needed to keep a system table's indexes in step when a row is appended to it: Access
    /// enumerates database objects through MSysObjects' <c>ParentIdName</c> index, so a row
    /// that is on the data page but absent from that index is invisible to Access even
    /// though this library's own full page scan still finds it.
    /// </para>
    /// </summary>

    /// <summary>
    /// Whether adding this entry would exceed what this writer can maintain — that is, whether
    /// it needs a tree deeper than root-node-plus-leaves, or a page whose entries this writer
    /// cannot re-emit. Reads only; nothing is modified.
    /// <para>
    /// A leaf split is fine, in a tree this library grew and in one Access wrote: the halves and
    /// the parent entry are written in Access's own shape, and Access seeks, ranges and
    /// aggregates over the result. Two cases still are not:
    /// </para>
    /// <list type="bullet">
    ///   <item>a leaf Access <em>prefix-compressed</em> — expanding those entries and re-emitting
    ///         them in full leaves Access unable to seek the page, though its aggregates still
    ///         answer. Pages this library writes are never compressed;</item>
    ///   <item>a split that would have to grow a third level, i.e. one whose new parent entry
    ///         does not fit in the root node. Splitting a node is not written yet.</item>
    /// </list>
    /// </summary>
    public bool WouldExceedIndexCapacity(TableDefinition table, Index index,
                                         IReadOnlyList<object?> values)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (index is null) throw new ArgumentNullException(nameof(index));

        int rootPage = index.RootPageNumber;
        if (rootPage <= 0) return false;

        var    format   = _file.Format;
        byte[] keyBytes = EncodeEntryKey(values);
        byte[] root     = _file.ReadPage(rootPage);

        int leafPage = root[0] == JetFormat.PageTypeIndexLeaf
            ? rootPage
            : DescendToLeaf(rootPage, keyBytes);

        byte[] leaf = _file.ReadPage(leafPage);

        if (ByteUtil.GetUShort(leaf, format.OffsetIndexCompressedByteCount) > 0) return true;

        var entries = ReadEntries(leaf, format, isLeaf: true);

        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        // One bit of the entry mask marks each entry's end, so the mask caps the entry count.
        int maxEntries      = format.SizeIndexEntryMask * 8;
        int totalBytes      = entries.Sum(e => e.RawBytes.Length) + keyBytes.Length + LeafTrailerLen;
        bool leafWillSplit  = totalBytes > entriesAreaSize || entries.Count + 1 > maxEntries;

        if (!leafWillSplit) return false;

        // The split has to be absorbed one level up. A root that is still a leaf becomes the
        // first node of a two-level tree, which always fits. A root that is already a node takes
        // one more child entry, and if that does not fit the node itself would have to split.
        if (root[0] == JetFormat.PageTypeIndexLeaf) return false;

        if (ByteUtil.GetUShort(root, format.OffsetIndexCompressedByteCount) > 0) return true;

        var  nodeEntries = ReadEntries(root, format, isLeaf: false);
        int  nodeBytes   = nodeEntries.Sum(e => e.RawBytes.Length) + keyBytes.Length + NodeTrailerLen;
        return nodeBytes > entriesAreaSize || nodeEntries.Count + 1 > maxEntries;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the entry count Access keeps for an index, in the
    /// index-definition block at the top of the TDEF.
    /// <para>
    /// Access answers <c>COUNT(*)</c> and <c>MAX(…)</c> from an index rather than by scanning,
    /// and a count of zero against a tree that actually holds entries makes it fail those with
    /// "Invalid argument" — even though a seek through the same index works.
    /// </para>
    /// </summary>
    public void IncrementIndexRowCount(TableDefinition table, Index index, int delta = 1)
        => IncrementIndexRowCountForDataBlock(table, index.IndexDataNumber, delta);

    /// <summary>
    /// The same increment addressed by index-data block rather than by an <see cref="Index"/>,
    /// for a table created in this session: its TDEF blocks have not been read back yet, so it
    /// has no <see cref="Index"/> metadata, only the one block it just wrote.
    /// </summary>
    public void IncrementIndexRowCountForDataBlock(TableDefinition table, int dataBlockNumber, int delta = 1)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(table.TdefPageNumber);

        int numIndexes = ByteUtil.GetInt(page, format.TdefOffsetNumIndexes);
        if (numIndexes < 1 || dataBlockNumber < 0 || dataBlockNumber >= numIndexes) return;

        // Block layout: 4 unknown bytes, then the 4-byte entry count.
        int countOffset = format.SizeTdefHeader + dataBlockNumber * format.SizeIndexDefinition + 4;
        if (countOffset + 4 > page.Length) return;

        ByteUtil.PutInt(page, countOffset, ByteUtil.GetInt(page, countOffset) + delta);
        _file.WritePage(table.TdefPageNumber, page);
    }

    /// <summary>Encodes an index entry's key: one value uses the single-column form.</summary>
    private static byte[] EncodeEntryKey(IReadOnlyList<object?> values)
        => values.Count == 1 && values[0] is not null
            ? EncodeKeyBytes(values[0]!)
            : EncodeCompositeKeyBytes(values);

    public void InsertIntoIndex(TableDefinition table, Index index,
                                IReadOnlyList<object?> values, int rowPointer)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (index is null) throw new ArgumentNullException(nameof(index));
        if (values is null || values.Count == 0)
            throw new ArgumentException("An index entry needs at least one column value.", nameof(values));

        int rootPage = index.RootPageNumber;
        if (rootPage <= 0) return;   // index with no tree to maintain

        byte[] keyBytes   = EncodeEntryKey(values);
        byte[] root       = _file.ReadPage(rootPage);
        bool   rootIsLeaf = root[0] == JetFormat.PageTypeIndexLeaf;

        if (rootIsLeaf)
        {
            var split = InsertIntoLeaf(rootPage, keyBytes, rowPointer);
            if (split is null) return;

            // Promote: a node above the two halves becomes the new root, and the TDEF has to
            // point at it — patching this index's own block, not blindly the first one.
            int newRoot = CreateRootNode(
                leftPage:      rootPage,
                leftMaxKey:    split.Value.LeftMaxKey,
                leftMaxRowPtr: split.Value.LeftMaxRowPtr,
                rightPage:     split.Value.NewSiblingPage);
            index.RootPageNumber = newRoot;
            if (index.IsPrimaryKey) table.PrimaryKeyIndexPage = newRoot;
            PatchTdefRoot(table, index, newRoot);
            return;
        }

        int leafPage  = DescendToLeaf(rootPage, keyBytes);
        var leafSplit = InsertIntoLeaf(leafPage, keyBytes, rowPointer);
        if (leafSplit is null) return;

        // The root is already a node, so the split is absorbed there — no TDEF patch needed.
        UpdateNodeForLeafSplit(rootPage,
            oldChildPage: leafPage,
            oldChildNewKey: leafSplit.Value.LeftMaxKey,
            oldChildMaxRowPtr: leafSplit.Value.LeftMaxRowPtr,
            newChildPage: leafSplit.Value.NewSiblingPage,
            newChildKey: leafSplit.Value.RightMaxKey,
            newChildMaxRowPtr: leafSplit.Value.RightMaxRowPtr);
    }

    private void InsertPrimaryKeyBytes(TableDefinition table, byte[] keyBytes, int rowPointer)
    {
        if (table.PrimaryKeyIndexPage == 0)
            throw new InvalidOperationException(
                "Table has no primary key index page. " +
                "Specify a primary key column name when calling Database.CreateTable.");

        var format = _file.Format;

        int rootPage = table.PrimaryKeyIndexPage;
        byte[] root = _file.ReadPage(rootPage);
        bool rootIsLeaf = root[0] == JetFormat.PageTypeIndexLeaf;

        if (rootIsLeaf)
        {
            // Two-level path: try inserting into the root leaf directly.
            var split = InsertIntoLeaf(rootPage, keyBytes, rowPointer);
            if (split is null) return;

            // Leaf split — promote: create a node above pointing at both halves.
            int newRoot = CreateRootNode(
                leftPage:      rootPage,
                leftMaxKey:    split.Value.LeftMaxKey,
                leftMaxRowPtr: split.Value.LeftMaxRowPtr,
                rightPage:     split.Value.NewSiblingPage);
            table.PrimaryKeyIndexPage = newRoot;

            // Patch the primary key's own block. A table this library created has one index and
            // so uses block 0; a table read back from disk carries the block on its metadata,
            // and Access does not order the blocks to match the slots.
            var pk = table.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
            if (pk is null)
            {
                PatchTdefRootForDataBlock(table, 0, newRoot);
                return;
            }

            pk.RootPageNumber = newRoot;
            PatchTdefRoot(table, pk, newRoot);
            return;
        }

        // Three-level path: root is a node. Descend to the right leaf, insert, and
        // if it splits, update the node entry's key + insert a new entry.
        int leafPage = DescendToLeaf(rootPage, keyBytes);
        var leafSplit = InsertIntoLeaf(leafPage, keyBytes, rowPointer);
        if (leafSplit is null) return;

        // Update node to reflect the leaf split: child's key shrinks to LeftMaxKey,
        // and we add a new entry pointing at the new sibling with RightMaxKey.
        UpdateNodeForLeafSplit(rootPage,
            oldChildPage: leafPage,
            oldChildNewKey: leafSplit.Value.LeftMaxKey,
            oldChildMaxRowPtr: leafSplit.Value.LeftMaxRowPtr,
            newChildPage: leafSplit.Value.NewSiblingPage,
            newChildKey: leafSplit.Value.RightMaxKey,
            newChildMaxRowPtr: leafSplit.Value.RightMaxRowPtr);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert a single (keyBytes, rowPtr) into the leaf at <paramref name="leafPage"/>.
    /// Returns null if the entry fit; otherwise the split info to propagate upward.
    /// </summary>
    private LeafSplit? InsertIntoLeaf(int leafPage, byte[] keyBytes, int rowPointer)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(leafPage);
        var entries = ReadEntries(page, format, isLeaf: true);

        var newEntry = BuildLeafEntry(keyBytes, rowPointer);
        InsertSortedLeaf(entries, newEntry);

        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        int totalBytes = entries.Sum(e => e.RawBytes.Length);

        if (totalBytes <= entriesAreaSize && entries.Count <= 3624)
        {
            // Fits — rewrite the whole leaf in place.
            WriteEntries(leafPage, entries, format, isLeaf: true);
            return null;
        }

        // Doesn't fit — split into two leaves.
        int mid = entries.Count / 2;
        var left  = entries.GetRange(0, mid);
        var right = entries.GetRange(mid, entries.Count - mid);

        // Allocate the right sibling and stitch into the next-pointer chain.
        int rightPage = _allocator.AllocatePage();
        byte[] rightPg = BuildEmptyIndexPage(format, isLeaf: true);
        int origNext = ByteUtil.GetInt(page, format.OffsetNextIndexPage);
        ByteUtil.PutInt(rightPg, format.OffsetPrevIndexPage, leafPage);
        ByteUtil.PutInt(rightPg, format.OffsetNextIndexPage, origNext);
        _file.WritePage(rightPage, rightPg);

        ByteUtil.PutInt(page, format.OffsetNextIndexPage, rightPage);
        _file.WritePage(leafPage, page);

        WriteEntries(leafPage,  left,  format, isLeaf: true);
        WriteEntries(rightPage, right, format, isLeaf: true);

        return new LeafSplit(
            LeftMaxKey:     left[^1].KeyBytes,
            LeftMaxRowPtr:  left[^1].RowPtr,
            RightMaxKey:    right[^1].KeyBytes,
            RightMaxRowPtr: right[^1].RowPtr,
            NewSiblingPage: rightPage);
    }

    /// <summary>
    /// Builds the node that goes above a split root.
    /// <para>
    /// Access shapes a node as "an entry per child <b>except the last</b>, plus a child-tail
    /// pointer at the final child". Listing every child as an entry and leaving the tail unset
    /// produced a tree Access could scan but not seek through — it reported "Invalid argument"
    /// on any lookup. Each entry also carries its child's greatest row pointer; zeroes there
    /// are fine for our own reader but not for Access.
    /// </para>
    /// </summary>
    private int CreateRootNode(int leftPage, byte[] leftMaxKey, int leftMaxRowPtr, int rightPage)
    {
        var format = _file.Format;
        int newRootPage = _allocator.AllocatePage();
        byte[] node = BuildEmptyIndexPage(format, isLeaf: false);

        // The right half becomes the child tail, so only the left child gets an entry.
        ByteUtil.PutInt(node, format.OffsetChildTailIndexPage, rightPage);
        _file.WritePage(newRootPage, node);

        WriteEntries(newRootPage,
                     new List<RawEntry> { BuildNodeEntry(leftMaxKey, leftMaxRowPtr, leftPage) },
                     format, isLeaf: false);
        return newRootPage;
    }

    /// <summary>
    /// Records a leaf split in the node above it, keeping Access's shape: every child except
    /// the last has an entry, and the last is reached through the page's child-tail pointer.
    /// </summary>
    private void UpdateNodeForLeafSplit(
        int nodePage, int oldChildPage, byte[] oldChildNewKey, int oldChildMaxRowPtr,
        int newChildPage, byte[] newChildKey, int newChildMaxRowPtr)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(nodePage);
        var entries = ReadEntries(page, format, isLeaf: false);

        int childTail = ByteUtil.GetInt(page, format.OffsetChildTailIndexPage);

        if (childTail == oldChildPage)
        {
            // The tail split. Its left half now has a bounded key range, so it becomes a
            // regular entry (appended last — it sorts above every existing child), and the
            // new right half takes over as the tail.
            entries.Add(BuildNodeEntry(oldChildNewKey, oldChildMaxRowPtr, oldChildPage));
            ByteUtil.PutInt(page, format.OffsetChildTailIndexPage, newChildPage);
            _file.WritePage(nodePage, page);
        }
        else
        {
            // An interior child split: refresh its entry's key and slot the new sibling in
            // straight after it, since the new keys fall between this child and the next.
            int at = entries.FindIndex(e => e.SubPage == oldChildPage);
            if (at < 0)
                throw new InvalidOperationException(
                    $"Node p{nodePage} has no entry for child p{oldChildPage}, so its split cannot be recorded.");

            entries[at] = BuildNodeEntry(oldChildNewKey, oldChildMaxRowPtr, oldChildPage);
            entries.Insert(at + 1, BuildNodeEntry(newChildKey, newChildMaxRowPtr, newChildPage));
        }

        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        int totalBytes = entries.Sum(e => e.RawBytes.Length);
        if (totalBytes > entriesAreaSize || entries.Count > 3624)
            throw new NotSupportedException(
                $"Root node is full ({entries.Count} children). " +
                "Tree depths greater than 2 (root node + leaves) are not yet supported.");

        WriteEntries(nodePage, entries, format, isLeaf: false);
    }

    /// <summary>
    /// Descends from <paramref name="startPage"/> (root) to the leaf whose key range covers
    /// <paramref name="searchKey"/>: the first entry with <c>entry.key &gt;= searchKey</c> wins.
    /// <para>
    /// When no entry qualifies the key belongs to the node's last child, which Access keeps in
    /// the page's child-tail pointer rather than as an entry — so that is where descent has to
    /// go. Falling back to the last <em>entry</em> instead skips a whole leaf.
    /// </para>
    /// </summary>
    private int DescendToLeaf(int startPage, byte[] searchKey)
    {
        var format = _file.Format;
        int cur = startPage;
        while (true)
        {
            byte[] page = _file.ReadPage(cur);
            if (page[0] == JetFormat.PageTypeIndexLeaf) return cur;
            if (page[0] != JetFormat.PageTypeIndexNode) return cur;

            var entries = ReadEntries(page, format, isLeaf: false);
            int target = -1;
            foreach (var e in entries)
            {
                if (CompareBytes(e.KeyBytes, searchKey) >= 0)
                {
                    target = e.SubPage;
                    break;
                }
            }
            if (target < 0)
            {
                int childTail = ByteUtil.GetInt(page, format.OffsetChildTailIndexPage);
                target = childTail > 0 && childTail != NoPage
                    ? childTail
                    : entries[^1].SubPage;   // older trees we wrote have no tail pointer
            }
            cur = target;
        }
    }

    /// <summary>
    /// Reads all entries from a leaf or node page, decoded into <see cref="RawEntry"/>
    /// records holding <b>full</b> keys, in on-disk order (which our writer keeps sorted
    /// ascending).
    /// <para>
    /// Access compresses a page by storing the leading bytes its entries share only once:
    /// the first entry is written whole and the page records how many of its leading bytes
    /// the rest omit. This expands that, because <see cref="WriteEntries"/> rewrites the
    /// area with full entries and a zeroed prefix count. Treating the stored bytes as whole
    /// keys instead — which this did — silently rewrote every one of Access's entries as a
    /// truncated key, so a value Access had indexed became unfindable, by Access and by this
    /// library alike. A numeric index usually has no shared prefix, which is why only text
    /// indexes showed it.
    /// </para>
    /// </summary>
    private static List<RawEntry> ReadEntries(byte[] page, JetFormat format, bool isLeaf)
    {
        var result = new List<RawEntry>();
        int trailerLen   = isLeaf ? LeafTrailerLen : NodeTrailerLen;
        int entryMaskPos = format.OffsetIndexEntryMask;
        int maskLen      = format.SizeIndexEntryMask;
        int entriesPos   = entryMaskPos + maskLen;

        int     prefixLen    = ByteUtil.GetUShort(page, format.OffsetIndexCompressedByteCount);
        byte[]? sharedPrefix = null;

        int lastStart = 0;
        for (int i = 0; i < maskLen; i++)
        {
            byte b = page[entryMaskPos + i];
            if (b == 0) continue;
            for (int j = 0; j < 8; j++)
            {
                if ((b & (1 << j)) == 0) continue;
                int endOffset = i * 8 + j;
                int entryLen  = endOffset - lastStart;
                int entryAbs  = entriesPos + lastStart;
                if (entryLen >= trailerLen)
                {
                    int storedKeyLen = entryLen - trailerLen;

                    // The first entry is stored whole and defines the shared prefix; the rest
                    // omit it and have to have it put back.
                    byte[] key;
                    if (result.Count == 0)
                    {
                        key = new byte[storedKeyLen];
                        Array.Copy(page, entryAbs, key, 0, storedKeyLen);
                        if (prefixLen > 0 && storedKeyLen >= prefixLen)
                        {
                            sharedPrefix = new byte[prefixLen];
                            Array.Copy(page, entryAbs, sharedPrefix, 0, prefixLen);
                        }
                    }
                    else if (sharedPrefix is not null)
                    {
                        key = new byte[sharedPrefix.Length + storedKeyLen];
                        Array.Copy(sharedPrefix, 0, key, 0, sharedPrefix.Length);
                        Array.Copy(page, entryAbs, key, sharedPrefix.Length, storedKeyLen);
                    }
                    else
                    {
                        key = new byte[storedKeyLen];
                        Array.Copy(page, entryAbs, key, 0, storedKeyLen);
                    }

                    int pgBE = (page[entryAbs + storedKeyLen]     << 16)
                             | (page[entryAbs + storedKeyLen + 1] <<  8)
                             |  page[entryAbs + storedKeyLen + 2];
                    int row  = page[entryAbs + storedKeyLen + 3];
                    int subPage = 0;
                    if (!isLeaf)
                    {
                        subPage = (page[entryAbs + storedKeyLen + 4] << 24)
                                | (page[entryAbs + storedKeyLen + 5] << 16)
                                | (page[entryAbs + storedKeyLen + 6] <<  8)
                                |  page[entryAbs + storedKeyLen + 7];
                    }

                    // Rebuild the on-disk form from the *full* key so WriteEntries emits it
                    // whole, matching the zeroed prefix count it writes.
                    var raw = new byte[key.Length + trailerLen];
                    Array.Copy(key, 0, raw, 0, key.Length);
                    Array.Copy(page, entryAbs + storedKeyLen, raw, key.Length, trailerLen);

                    result.Add(new RawEntry(key, raw, (pgBE << 16) | row, subPage));
                }
                lastStart = endOffset;
            }
        }
        return result;
    }

    private void WriteEntries(int pageNumber, List<RawEntry> entries, JetFormat format, bool isLeaf)
    {
        byte[] page = _file.ReadPage(pageNumber);

        // Clear entry mask + entries area.
        int entryMaskPos = format.OffsetIndexEntryMask;
        int maskLen      = format.SizeIndexEntryMask;
        int entriesPos   = entryMaskPos + maskLen;
        for (int i = 0; i < maskLen; i++) page[entryMaskPos + i] = 0;
        int areaSize = format.PageSize - entriesPos;
        for (int i = 0; i < areaSize; i++) page[entriesPos + i] = 0;

        int cursor = 0;
        foreach (var e in entries)
        {
            Array.Copy(e.RawBytes, 0, page, entriesPos + cursor, e.RawBytes.Length);
            cursor += e.RawBytes.Length;
            int endPos = cursor;
            page[entryMaskPos + endPos / 8] |= (byte)(1 << (endPos % 8));
        }
        ByteUtil.PutShort(page, 2, (short)(areaSize - cursor));

        // Every entry above was written in full, so the page has no shared prefix. That
        // field must be cleared, not left as we found it: an index page Access wrote may
        // declare a prefix (its entries omit those leading bytes), and re-writing the area
        // with full entries while the count still says N makes every reader — Access
        // included — strip N bytes that are really key data. The rowIds it then extracts
        // are garbage, which surfaces as "Not a valid bookmark".
        ByteUtil.PutShort(page, format.OffsetIndexCompressedByteCount, 0);

        // Re-establish page-type bytes (BuildEmptyIndexPage sets them, but we read
        // existing pages on the write path).
        page[0] = isLeaf ? JetFormat.PageTypeIndexLeaf : JetFormat.PageTypeIndexNode;
        page[1] = 0x01;
        _file.WritePage(pageNumber, page);
    }

    /// <summary>
    /// Inserts <paramref name="entry"/> into <paramref name="entries"/> at the
    /// position that keeps the list sorted ascending by key bytes.
    /// </summary>
    private static void InsertSortedLeaf(List<RawEntry> entries, RawEntry entry)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (CompareBytes(entries[mid].KeyBytes, entry.KeyBytes) <= 0) lo = mid + 1;
            else hi = mid;
        }
        entries.Insert(lo, entry);
    }

    // ── Entry builders ────────────────────────────────────────────────────────

    private static RawEntry BuildLeafEntry(byte[] keyBytes, int rowPointer)
    {
        int pageNum = (rowPointer >> 16) & 0xFFFFFF;
        int rowNum  = rowPointer & 0xFF;
        var raw = new byte[keyBytes.Length + LeafTrailerLen];
        Array.Copy(keyBytes, raw, keyBytes.Length);
        raw[keyBytes.Length    ] = (byte)((pageNum >> 16) & 0xFF);
        raw[keyBytes.Length + 1] = (byte)((pageNum >>  8) & 0xFF);
        raw[keyBytes.Length + 2] = (byte) (pageNum        & 0xFF);
        raw[keyBytes.Length + 3] = (byte)  rowNum;
        return new RawEntry(keyBytes, raw, rowPointer, 0);
    }

    /// <summary>
    /// Builds a node entry: key, the child's greatest row pointer, then the child page.
    /// Access stores that row pointer and will not seek through a tree whose node entries
    /// carry zeroes there, even though our own reader ignores it.
    /// </summary>
    private static RawEntry BuildNodeEntry(byte[] keyBytes, int maxRowPointer, int subPage)
    {
        var raw = new byte[keyBytes.Length + NodeTrailerLen];
        Array.Copy(keyBytes, raw, keyBytes.Length);

        int rowPage = (maxRowPointer >> 16) & 0xFFFFFF;
        int rowNum  =  maxRowPointer & 0xFFFF;
        raw[keyBytes.Length    ] = (byte)((rowPage >> 16) & 0xFF);
        raw[keyBytes.Length + 1] = (byte)((rowPage >>  8) & 0xFF);
        raw[keyBytes.Length + 2] = (byte) (rowPage        & 0xFF);
        raw[keyBytes.Length + 3] = (byte)  rowNum;

        raw[keyBytes.Length + 4] = (byte)((subPage >> 24) & 0xFF);
        raw[keyBytes.Length + 5] = (byte)((subPage >> 16) & 0xFF);
        raw[keyBytes.Length + 6] = (byte)((subPage >>  8) & 0xFF);
        raw[keyBytes.Length + 7] = (byte) (subPage        & 0xFF);
        return new RawEntry(keyBytes, raw, 0, subPage);
    }

    // ── TDEF root-page patch ──────────────────────────────────────────────────

    /// <summary>
    /// After a root-page change (a leaf promoted to a node), update the TDEF's index column
    /// block so the new root persists. Layout per Jackcess: header
    /// + numIndexes×SizeIndexDefinition + numCols×SizeColumnHeader + column-names → the
    /// index column blocks, each SizeIndexColumnBlock bytes; within a block the root-page
    /// field sits at <c>SkipBeforeIndex + 30 + 4</c> (4 magic + 10×3 column entries
    /// + 4 usage-map ref).
    /// </summary>
    /// <param name="index">
    /// The index whose root moved. Its <see cref="Index.IndexDataNumber"/> selects the block —
    /// not its position in the table's index list, which Access orders independently: in
    /// <c>common1V2000.mdb</c> the primary key is the second slot but owns the first block, so
    /// addressing blocks by slot repointed the primary key at the secondary index's tree.
    /// </param>
    private void PatchTdefRoot(TableDefinition table, Index index, int newRoot)
        => PatchTdefRootForDataBlock(table, index.IndexDataNumber, newRoot);

    /// <summary>
    /// The same patch addressed by index-data block, for a table created in this session: it has
    /// no <see cref="Index"/> metadata yet, only the single block it just wrote.
    /// </summary>
    private void PatchTdefRootForDataBlock(TableDefinition table, int dataBlock, int newRoot)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(table.TdefPageNumber);

        int numIndexes = ByteUtil.GetInt(page, format.TdefOffsetNumIndexes);
        if (numIndexes < 1 || dataBlock < 0 || dataBlock >= numIndexes) return;
        int numCols    = ByteUtil.GetShort(page, format.TdefOffsetNumCols);
        int colHdrSize = format.SizeColumnHeader;

        int colDefStart  = format.SizeTdefHeader + numIndexes * format.SizeIndexDefinition;
        int colNamesPos  = colDefStart + numCols * colHdrSize;

        // Walk past column names to land on the first index column block.
        int pos = colNamesPos;
        for (int i = 0; i < numCols && pos + format.SizeNameLength <= page.Length; i++)
        {
            int nameLen = format.SizeNameLength == 2
                ? ByteUtil.GetShort(page, pos)
                : page[pos];
            pos += format.SizeNameLength + nameLen;
        }

        int blockStart      = pos + dataBlock * format.SizeIndexColumnBlock;
        int rootFieldOffset = blockStart + format.SkipBeforeIndex + 30 + 4;
        if (rootFieldOffset + 4 > page.Length) return;

        ByteUtil.PutInt(page, rootFieldOffset, newRoot);
        _file.WritePage(table.TdefPageNumber, page);
    }

    // ── Helpers shared with IndexReader ──────────────────────────────────────

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

    // ── Key encoding ──────────────────────────────────────────────────────────

    private static byte[] EncodeKeyBytes(object value)
    {
        // Single column: ascending flag + per-type value bytes.
        byte[] valueBytes = EncodeColumnValueBytes(value);
        var buf = new byte[1 + valueBytes.Length];
        buf[0] = AscStartFlag;
        Buffer.BlockCopy(valueBytes, 0, buf, 1, valueBytes.Length);
        return buf;
    }

    /// <summary>
    /// Multi-column key encoder: every column is framed with its own start-flag byte —
    /// <c>[flag][col0][flag][col1]…</c> — and a null column collapses to a lone null-flag
    /// byte, matching <c>IndexReader.EncodeColumnKey</c> and therefore Access.
    /// <para>
    /// This used to emit one flag for the whole key and concatenate the column values after
    /// it. That is self-consistent — the writer's own lookups encode search keys the same
    /// way, so composite primary keys round-tripped and the tests passed — but it is not
    /// Jet's format. Entries written that way are invisible to Access *and* to
    /// <c>IndexReader</c>, which is why a row appended to MSysObjects never showed up as a
    /// table: Access enumerates objects through the ParentIdName index.
    /// </para>
    /// </summary>
    internal static byte[] EncodeCompositeKeyBytes(IReadOnlyList<object?> values)
    {
        if (values is null || values.Count == 0)
            throw new ArgumentException("Composite key needs at least one value.", nameof(values));

        var parts = new byte[values.Count][];
        int total = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
            {
                parts[i] = new[] { AscNullFlag };
            }
            else
            {
                byte[] valueBytes = EncodeColumnValueBytes(values[i]!);
                parts[i]    = new byte[1 + valueBytes.Length];
                parts[i][0] = AscStartFlag;
                Buffer.BlockCopy(valueBytes, 0, parts[i], 1, valueBytes.Length);
            }
            total += parts[i].Length;
        }

        var buf = new byte[total];
        int pos = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            Buffer.BlockCopy(parts[i], 0, buf, pos, parts[i].Length);
            pos += parts[i].Length;
        }
        return buf;
    }

    /// <summary>
    /// Per-type value-bytes encoder (without the leading ascending flag).
    /// Used by both <see cref="EncodeKeyBytes"/> and <see cref="EncodeCompositeKeyBytes"/>.
    /// </summary>
    private static byte[] EncodeColumnValueBytes(object value) =>
        value switch
        {
            byte   v => new[] { v },
            short  v => EncodeAscInt16Bytes(v),
            int    v => EncodeAscInt32Bytes(v),
            long   v => EncodeAscInt64Bytes(v),
            string s => GeneralLegacyIndexCodes.EncodeText(s, isAscending: true),
            Guid   g => g.ToByteArray(),
            _ => throw new NotSupportedException(
                    $"Primary key encoding for type '{value.GetType().Name}' is not yet supported.")
        };

    private static byte[] EncodeAscInt16Bytes(short value)
    {
        ushort v = (ushort)((ushort)value ^ 0x8000u);
        return new[] { (byte)(v >> 8), (byte)v };
    }

    private static byte[] EncodeAscInt32Bytes(int value)
    {
        uint v = (uint)value ^ 0x80000000u;
        return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static byte[] EncodeAscInt64Bytes(long value)
    {
        ulong v = (ulong)value ^ 0x8000000000000000ul;
        return new[]
        {
            (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32),
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >>  8), (byte)v
        };
    }

    // ── Page builders ────────────────────────────────────────────────────────

    private static byte[] BuildEmptyIndexPage(JetFormat format, bool isLeaf)
    {
        var page = new byte[format.PageSize];
        page[0] = isLeaf ? JetFormat.PageTypeIndexLeaf : JetFormat.PageTypeIndexNode;
        page[1] = 0x01;
        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        ByteUtil.PutShort(page, 2, (short)entriesAreaSize);
        // "No page" is written as 0, which is what Access writes — page 0 is the database
        // header and can never be an index page, so it is unambiguous. Writing 0xFFFFFFFF here
        // instead let seeks and range scans work but broke whole-index operations: Access walks
        // the leaf chain to its end for COUNT(*) and MAX(...), and a -1 link is not a
        // terminator to it, so both failed with "Invalid argument".
        ByteUtil.PutInt(page, format.OffsetPrevIndexPage,      0);
        ByteUtil.PutInt(page, format.OffsetNextIndexPage,      0);
        ByteUtil.PutInt(page, format.OffsetChildTailIndexPage, 0);
        return page;
    }

    // ── Records ───────────────────────────────────────────────────────────────

    private record struct RawEntry(byte[] KeyBytes, byte[] RawBytes, int RowPtr, int SubPage);

    private record struct LeafSplit(byte[] LeftMaxKey, int LeftMaxRowPtr,
                                    byte[] RightMaxKey, int RightMaxRowPtr,
                                    int NewSiblingPage);
}
