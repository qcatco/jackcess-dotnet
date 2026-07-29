using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Adds one index to a table definition that is already on disk, by splicing the new index's
/// sections into it.
/// <para>
/// A TDEF interleaves per-index data with per-column data, so an index is not one contiguous
/// record that could be appended: its row-count block sits <em>before</em> the column
/// definitions, its column block and slot come <em>after</em> the column names, and its name
/// follows the other index names. Adding one therefore shifts most of the page.
/// </para>
/// <para>
/// This rebuilds the page section by section, copying every existing byte through unchanged and
/// appending the new index at the end of each of its four sections. Copying rather than
/// re-serialising is deliberate: the exact bytes of a table Access can already read have been
/// established one field at a time against the ACE engine, and re-deriving them would put all of
/// that at risk to add one index. Appending at the end of each section also makes the new index
/// the last slot <em>and</em> the last data block, so the two agree — see
/// <see cref="Index.IndexDataNumber"/> for why that cannot be assumed in general.
/// </para>
/// </summary>
internal static class TdefIndexAppender
{
    /// <summary>10 column slots per index column block, as Access always writes.</summary>
    private const int  MaxIndexColumns     = 10;
    private const short ColumnUnused       = unchecked((short)0xFFFF);
    private const byte AscendingColumnFlag = 0x01;
    private const byte UniqueIndexFlag     = 0x01;
    private const byte UnknownIndexFlag    = 0x80;   // always set on Access 2000+ indexes
    private const byte RegularIndexType    = 0x00;   // 1 = primary key, 2 = foreign key
    private const int  InvalidIndexNumber  = -1;
    private const int  MagicTableNumber    = 1625;

    /// <summary>What the new index needs written into it, resolved against the table already.</summary>
    internal sealed record NewIndex(
        string Name,
        IReadOnlyList<short> ColumnNumbers,
        int RootPage,
        int UmapPage,
        int UmapRow,
        bool IsUnique);

    /// <summary>
    /// Rewrites the table definition rooted at <paramref name="tdefPage"/> to include
    /// <paramref name="index"/>, extending it onto a continuation page if the new sections no
    /// longer fit.
    /// </summary>
    internal static void Append(PageFile file, PageAllocator allocator, int tdefPage, NewIndex index)
    {
        var format = file.Format;

        // The definition may continue on further pages; work on the whole thing as one buffer and
        // let TdefChain lay it back out, growing the chain if the new sections no longer fit.
        var (old, pages) = TdefChain.Read(file, tdefPage);

        int numCols       = ByteUtil.GetShort(old, format.TdefOffsetNumCols);
        int numIndexes    = ByteUtil.GetInt(old, format.TdefOffsetNumIndexes);
        int numIndexSlots = ByteUtil.GetInt(old, format.TdefOffsetNumIndexSlots);

        byte[] nameBytes = Encoding.Unicode.GetBytes(index.Name);
        int    added     = format.SizeIndexDefinition + format.SizeIndexColumnBlock
                         + format.SizeIndexInfoBlock + format.SizeNameLength + nameBytes.Length;

        int contentSize = ByteUtil.GetInt(old, 8);

        // ── Locate the section boundaries in the existing page ────────────────
        int idxDefStart   = format.SizeTdefHeader;
        int colDefStart   = idxDefStart + numIndexes * format.SizeIndexDefinition;
        int colNamesStart = colDefStart + numCols * format.SizeColumnHeader;

        int pos = colNamesStart;
        for (int i = 0; i < numCols; i++) pos += NameLength(old, pos, format);
        int idxColBlocksStart = pos;

        int slotsStart    = idxColBlocksStart + numIndexes * format.SizeIndexColumnBlock;
        int idxNamesStart  = slotsStart + numIndexSlots * format.SizeIndexInfoBlock;

        pos = idxNamesStart;
        for (int i = 0; i < numIndexSlots; i++) pos += NameLength(old, pos, format);
        int tailStart = pos;   // LVAL usage-map refs + the 0xFF 0xFF trailer

        int tailLength = contentSize - tailStart;
        if (tailLength < 0)
            throw new NotSupportedException(
                $"Table definition on page {tdefPage} is shorter than its own sections " +
                $"(content {contentSize} bytes, sections end at {tailStart}).");

        // ── Rebuild, copying every existing byte and appending the new index ──
        var page = new byte[Math.Max(old.Length, contentSize + added)];
        Copy(old, 0, page, 0, format.SizeTdefHeader);   // page prefix + TDEF header

        int w = format.SizeTdefHeader;
        w = CopyRun(old, idxDefStart,   page, w, colDefStart - idxDefStart);     // row-count blocks
        w += format.SizeIndexDefinition;                                         // …and the new one (zeros)

        w = CopyRun(old, colDefStart,   page, w, idxColBlocksStart - colDefStart);   // columns + names
        w = CopyRun(old, idxColBlocksStart, page, w, slotsStart - idxColBlocksStart); // column blocks
        w = WriteColumnBlock(page, w, format, index);

        w = CopyRun(old, slotsStart, page, w, idxNamesStart - slotsStart);        // slots
        w = WriteSlot(page, w, format, indexNumber: numIndexSlots, dataNumber: numIndexes);

        w = CopyRun(old, idxNamesStart, page, w, tailStart - idxNamesStart);      // index names
        ByteUtil.PutShort(page, w, (short)nameBytes.Length); w += format.SizeNameLength;
        Copy(nameBytes, 0, page, w, nameBytes.Length); w += nameBytes.Length;

        w = CopyRun(old, tailStart, page, w, tailLength);                         // LVAL refs + trailer

        // ── Header fields the new index changes ───────────────────────────────
        ByteUtil.PutInt(page, 8, contentSize + added);
        ByteUtil.PutInt(page, format.TdefOffsetNumIndexes,    numIndexes + 1);
        ByteUtil.PutInt(page, format.TdefOffsetNumIndexSlots, numIndexSlots + 1);

        // Bytes 2-3 hold the room left on the *first* page; a definition that spills onto a
        // continuation page has none.
        int firstPageContent = Math.Min(contentSize + added, format.PageSize - 8);
        ByteUtil.PutShort(page, 2, (short)(format.PageSize - 8 - firstPageContent));

        TdefChain.Write(file, allocator, pages, page);
    }

    /// <summary>Emits the new index's column block: its columns, usage map, root page and flags.</summary>
    private static int WriteColumnBlock(byte[] page, int blockStart, JetFormat format, NewIndex index)
    {
        int p = blockStart + format.SkipBeforeIndex;

        for (int c = 0; c < MaxIndexColumns; c++)
        {
            bool used = c < index.ColumnNumbers.Count;
            ByteUtil.PutShort(page, p, used ? index.ColumnNumbers[c] : ColumnUnused); p += 2;
            page[p++] = used ? AscendingColumnFlag : (byte)0x00;
        }

        // Usage-map reference: 1-byte row + 3-byte page. Access rejects an indexed table whose
        // index has no page map ("Not a valid bookmark"), however few rows the table has.
        page[p++] = (byte)index.UmapRow;
        ByteUtil.Put3ByteInt(page, p, index.UmapPage); p += 3;

        ByteUtil.PutInt(page, p, index.RootPage); p += 4;
        p += format.SkipBeforeIndexFlags;
        // Not RequiredIndexFlag: a secondary index may hold nulls. Unique only when asked.
        page[p] = (byte)(UnknownIndexFlag | (index.IsUnique ? UniqueIndexFlag : 0));

        return blockStart + format.SizeIndexColumnBlock;
    }

    /// <summary>Emits the new index's logical slot, pointing at the column block just written.</summary>
    private static int WriteSlot(byte[] page, int slotStart, JetFormat format,
                                int indexNumber, int dataNumber)
    {
        // Access and Jackcess both open the slot with the magic table number the column
        // definitions carry; zeroes here left Access unable to answer COUNT(*)/MAX from the index.
        if (format.SkipBeforeIndexSlot >= 4)
            ByteUtil.PutInt(page, slotStart, MagicTableNumber);

        int p = slotStart + format.SkipBeforeIndexSlot;
        ByteUtil.PutInt(page, p, indexNumber);        p += 4;
        ByteUtil.PutInt(page, p, dataNumber);         p += 4;
        p += 1;                                                // relIndexType
        ByteUtil.PutInt(page, p, InvalidIndexNumber); p += 4;   // relIndexNumber
        p += 4;                                                // relTablePage
        p += 2;                                                // cascadeUpdates + cascadeDeletes
        page[p] = RegularIndexType;

        return slotStart + format.SizeIndexInfoBlock;
    }

    /// <summary>Total bytes a length-prefixed name occupies at <paramref name="pos"/>.</summary>
    private static int NameLength(byte[] page, int pos, JetFormat format)
    {
        int len = format.SizeNameLength == 2 ? ByteUtil.GetShort(page, pos) : page[pos];
        return format.SizeNameLength + len;
    }

    private static int CopyRun(byte[] src, int srcPos, byte[] dst, int dstPos, int length)
    {
        Copy(src, srcPos, dst, dstPos, length);
        return dstPos + length;
    }

    private static void Copy(byte[] src, int srcPos, byte[] dst, int dstPos, int length)
    {
        if (length <= 0) return;
        Array.Copy(src, srcPos, dst, dstPos, length);
    }
}
