using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// The order a Text or Memo column's values sort in, in its indexes, as its definition in the TDEF holds it: the
/// sort order's code (1033 for Access's "General" orders) and, in Jet4's four bytes, its version. Access 2000 to
/// 2007 sort in "General - Legacy" (version 0), Access 2010 and later make new databases in "General" (version 1),
/// and Access 97 has an order of its own. An index entry's key is the text in its column's order, so a key can be
/// written or searched for only in an order this library writes: General - Legacy.
/// </summary>
internal readonly record struct SortOrder(short Code, byte Version)
{
    private const short GeneralCode = 1033;

    // Not a version Jet4 writes: Access 97's two-byte sort orders have none.
    private const byte Access97Version = 0xFF;

    /// <summary>"General - Legacy": the order whose keys <see cref="GeneralLegacyIndexCodes"/> writes.</summary>
    public static SortOrder GeneralLegacy { get; } = new(GeneralCode, 0);

    public bool IsGeneralLegacy => this == GeneralLegacy;

    /// <summary>
    /// The sort order a column definition holds at <paramref name="at"/>: the code, then in Jet4 a byte this
    /// library does not use and the version. A code of 0 is what versions of this library before sort orders
    /// wrote, and they keyed such a column in General - Legacy, as Jackcess reads it.
    /// </summary>
    public static SortOrder Read(byte[] page, int at, JetFormat format)
    {
        short code = ByteUtil.GetShort(page, at);
        if (format.SizeSortOrder < 4) return new(code, Access97Version);
        if (code == 0) return GeneralLegacy;
        return new(code, page[at + 3]);
    }

    /// <summary>Writes this sort order where a column definition holds it, as <see cref="Read"/> reads it.</summary>
    public void Write(byte[] page, int at, JetFormat format)
    {
        ByteUtil.PutShort(page, at, Code);
        if (format.SizeSortOrder < 4) return;
        page[at + 2] = 0;
        page[at + 3] = Version;
    }

    public override string ToString()
        => IsGeneralLegacy ? "General - Legacy"
         : Code == GeneralCode && Version == 1 ? "General"
         : Version == Access97Version ? $"Access 97's order {Code}"
         : $"order {Code}, version {Version}";
}
