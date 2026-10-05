namespace JackcessDotNet;

/// <summary>
/// A row's address as Jet holds one: its page in the high 24 bits, its row on the page in the low 8 - the page
/// number an index entry stores in three bytes, the row in one. Shifted 16 bits instead, a page past 32,767 did not
/// fit, and a primary-key index pointed rows on such pages at the wrong page.
/// </summary>
internal static class RowPointer
{
    /// <summary>The highest page a pointer can hold: three bytes' worth, beyond any file Jet allows.</summary>
    public const int MaxPage = 0xFFFFFF;

    public static int Pack(int page, int row)
    {
        if (page < 0 || page > MaxPage)
            throw new ArgumentOutOfRangeException(nameof(page), page, "A row pointer holds a page of 0 to 16,777,215.");
        if (row < 0 || row > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(row), row, "A row pointer holds a row of 0 to 255.");
        return (int)(((uint)page << 8) | (uint)row);
    }

    public static int Page(int pointer) => (int)((uint)pointer >> 8);

    public static int Row(int pointer) => pointer & 0xFF;
}
