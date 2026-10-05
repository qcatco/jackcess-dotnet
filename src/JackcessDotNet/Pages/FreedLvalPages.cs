namespace JackcessDotNet;

/// <summary>
/// Long-value pages a delete or an update left room on, kept by the column whose value they held - the owned-pages
/// usage map that lists them - with the room each had when last seen, for that column's later values to use again
/// (LvalWriter). A write looks through its own column's alone, and reads only a page noted as having room for what it
/// writes: one set for the whole file had every long-value write, in any table, read every page any column had freed.
/// The pages a delete freed are also in the column's free-space usage map, as Access keeps them, and the first write
/// to a column in a session notes what that map lists (<see cref="FirstLookFor"/>).
/// </summary>
internal sealed class FreedLvalPages
{
    /// <summary>A page with less room than this is not worth reading for it, so it is not kept.</summary>
    internal const int MinUsefulFreeSpace = 64;

    /// <summary>The room noted for a page the column's free-space map lists but this session has not read.</summary>
    internal const int NotYetRead = int.MaxValue;

    private readonly Dictionary<(int MapPage, int MapRow), SortedDictionary<int, int>> _byColumn = new();
    private readonly HashSet<(int MapPage, int MapRow)> _looked = new();

    /// <summary>
    /// True the first time it is asked for the column whose owned-pages map is at (<paramref name="mapPage"/>,
    /// <paramref name="mapRow"/>) - when its free-space map is to be looked at - and false after.
    /// </summary>
    public bool FirstLookFor(int mapPage, int mapRow) => _looked.Add((mapPage, mapRow));

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
    /// Notes the room <paramref name="page"/> has now if the column has it noted already - a page it freed - and leaves
    /// any other page alone.
    /// </summary>
    public void Renew(int mapPage, int mapRow, int page, int freeSpace)
    {
        if (_byColumn.TryGetValue((mapPage, mapRow), out var pages) && pages.ContainsKey(page))
            Set(mapPage, mapRow, page, freeSpace);
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
