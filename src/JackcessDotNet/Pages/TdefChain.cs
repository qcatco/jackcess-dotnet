using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Assembles a table definition that Jet has spread over several pages into one contiguous buffer,
/// and writes such a buffer back out again.
/// <para>
/// A definition longer than a page continues on the page named at bytes 4-7, which in turn may name
/// another. Only the first page carries the TDEF header, so a continuation contributes just its
/// bytes past the 8-byte page prefix — concatenating those onto the first page gives a buffer in
/// which every offset the format defines still lands where it should, and the readers need no
/// notion of pages at all.
/// </para>
/// <para>
/// Reading only the first page instead — which is what happened before — leaves a wide table's index
/// sections beyond the end of the buffer. That was handled by returning no indexes at all, so a
/// table with more columns than fit a page silently appeared to have none, and appending to it left
/// every index untouched.
/// </para>
/// </summary>
internal static class TdefChain
{
    /// <summary>Bytes 4-7 of a definition page: the page continuing it, or 0.</summary>
    private const int OffsetNextPage = 4;

    /// <summary>
    /// A definition should need a handful of pages at most — Jet caps a table at 255 columns — so
    /// this only stops a file whose pointers loop from being followed forever.
    /// </summary>
    private const int MaxPages = 64;

    /// <summary>
    /// Reads the definition starting at <paramref name="firstPage"/> as one buffer, plus the pages
    /// it came from in order.
    /// </summary>
    internal static (byte[] Buffer, List<int> Pages) Read(PageFile file, int firstPage)
    {
        var    pages  = new List<int> { firstPage };
        byte[] first  = file.ReadPage(firstPage);
        int    next   = ByteUtil.GetInt(first, OffsetNextPage);

        if (next == 0) return (first, pages);

        int    prefix = 8;
        var    buffer = new List<byte>(first);
        var    seen   = new HashSet<int> { firstPage };

        while (next != 0)
        {
            if (pages.Count >= MaxPages || !seen.Add(next))
                throw new InvalidOperationException(
                    $"Table definition starting at page {firstPage} chains through more than " +
                    $"{MaxPages} pages or revisits one, so its continuation pointers are not sound.");

            byte[] page = file.ReadPage(next);
            pages.Add(next);
            buffer.AddRange(page.Skip(prefix));
            next = ByteUtil.GetInt(page, OffsetNextPage);
        }

        return (buffer.ToArray(), pages);
    }

    /// <summary>
    /// Writes <paramref name="buffer"/> back over <paramref name="pages"/>, allocating further
    /// continuation pages when it has outgrown them and keeping the chain's pointers right.
    /// <para>
    /// Each continuation page keeps its own 8-byte prefix — page type, and the pointer to whatever
    /// follows — so the buffer's bytes are laid down after it, mirroring how <see cref="Read"/>
    /// skipped it.
    /// </para>
    /// </summary>
    internal static void Write(PageFile file, PageAllocator allocator, IReadOnlyList<int> pages, byte[] buffer)
    {
        int pageSize = file.Format.PageSize;
        const int prefix = 8;

        // The first page holds pageSize bytes of the buffer; each continuation holds pageSize - 8.
        int needed = buffer.Length <= pageSize
            ? 1
            : 1 + (int)Math.Ceiling((buffer.Length - pageSize) / (double)(pageSize - prefix));

        var chain = new List<int>(pages);
        while (chain.Count < needed) chain.Add(allocator.AllocatePage());

        for (int i = 0; i < chain.Count; i++)
        {
            var page = new byte[pageSize];

            int from  = i == 0 ? 0 : pageSize + (i - 1) * (pageSize - prefix);
            int at    = i == 0 ? 0 : prefix;
            int count = Math.Min(pageSize - at, Math.Max(0, buffer.Length - from));
            if (count > 0) Array.Copy(buffer, from, page, at, count);

            if (i > 0)
            {
                page[0] = JetFormat.PageTypeTableDef;
                page[1] = 0x01;
            }

            // Point at the next page in the chain, or end it. A page left over from a shorter
            // definition is unlinked rather than followed.
            ByteUtil.PutInt(page, OffsetNextPage, i + 1 < needed ? chain[i + 1] : 0);
            file.WritePage(chain[i], page);
        }
    }
}
