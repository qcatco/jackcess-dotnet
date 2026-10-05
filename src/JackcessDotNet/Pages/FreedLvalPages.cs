namespace JackcessDotNet;

/// <summary>
/// Long-value pages a delete or an update left room on in this session, kept by the column whose value they held - the
/// owned-pages usage map that lists them - with the room each had when last seen, for that column's later values to
/// use again (LvalWriter). A write looks through its own column's alone, and reads only a page noted as having room for
/// what it writes: one set for the whole file had every long-value write, in any table, read every page any column
/// had freed.
/// </summary>
internal sealed class FreedLvalPages
{
    /// <summary>A page with less room than this is not worth reading for it, so it is not kept.</summary>
    internal const int MinUsefulFreeSpace = 64;

    private readonly Dictionary<(int MapPage, int MapRow), SortedDictionary<int, int>> _byColumn = new();

    /// <summary>
    /// Notes that <paramref name="page"/>, listed by the owned-pages map at (<paramref name="mapPage"/>,
    /// <paramref name="mapRow"/>), has <paramref name="freeSpace"/> bytes free - forgetting it when that is too little
    /// to use.
    /// </summary>
    public void Set(int mapPage, int mapRow, int page, int freeSpace)
    {
        var column = (mapPage, mapRow);
        if (freeSpace >= MinUsefulFreeSpace)
        {
            if (!_byColumn.TryGetValue(column, out var pages))
                _byColumn[column] = pages = new SortedDictionary<int, int>();
            pages[page] = freeSpace;
        }
        else if (_byColumn.TryGetValue(column, out var pages) && pages.Remove(page) && pages.Count == 0)
        {
            _byColumn.Remove(column);
        }
    }

    /// <summary>
    /// The pages of the column whose owned-pages map is at (<paramref name="mapPage"/>, <paramref name="mapRow"/>),
    /// lowest first, each with the room it had when last seen. Not to be enumerated across a <see cref="Set"/>.
    /// </summary>
    public IEnumerable<KeyValuePair<int, int>> Of(int mapPage, int mapRow)
        => _byColumn.TryGetValue((mapPage, mapRow), out var pages)
            ? pages
            : Enumerable.Empty<KeyValuePair<int, int>>();
}
