namespace JackcessDotNet;

/// <summary>
/// Usage-map references for one long-value (Memo/OLE) column, as stored in the
/// TDEF's LVAL section. Each column has TWO usage maps — owned LVAL pages and
/// LVAL pages with free space — and each reference is a (page, row) pair: the
/// row selects a usage-map record WITHIN the shared usage-map page (real Access
/// typically stores these on the table's own umap page at rows 2/3, after the
/// table's owned/free rows 0/1).
///
/// On-disk entry layout (10 bytes, 0xFFFF column-number terminated), matching
/// Java Jackcess TableImpl/UsageMap.read:
///   [0..1]  column number (LE)
///   [2]     owned-pages umap row      [3..5]  owned-pages umap page (3-byte LE)
///   [6]     free-space umap row       [7..9]  free-space umap page  (3-byte LE)
/// </summary>
public readonly record struct LvalUmapRef(
    int OwnedPage, int OwnedRow,
    int FreePage, int FreeRow);
