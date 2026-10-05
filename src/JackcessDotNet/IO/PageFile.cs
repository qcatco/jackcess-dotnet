using System.IO;

namespace JackcessDotNet;

public sealed class PageFile : IDisposable
{
    private readonly FileStream _stream;
    private readonly JetFormat _format;
    private readonly string _path;

    public JetFormat Format => _format;
    public long Length => _stream.Length;
    public int PageCount => (int)(_stream.Length / _format.PageSize);

    /// <summary>Pages read since the file was opened: what tests measure an operation's cost in.</summary>
    internal long ReadCount { get; private set; }

    // Pages read through ReadKeptPage, as last read or written here.
    private readonly Dictionary<int, byte[]> _kept = new();

    /// <summary>
    /// LVAL pages whose space a deleted long value freed in this session (LvalFree), by column, for that column's later
    /// values to use again (LvalWriter). A hint: a page is read, and checked against the writing column's usage map,
    /// before any row goes onto it.
    /// </summary>
    internal FreedLvalPages FreedLvalPages { get; } = new FreedLvalPages();

    /// <summary>
    /// Per-page codec applied on every Read/Write. Default is the identity
    /// (<see cref="NullCodecHandler.Instance"/>); <see cref="Database.Open"/> may
    /// install a different one for encrypted files (none implemented today).
    /// </summary>
    public ICodecHandler Codec { get; set; } = NullCodecHandler.Instance;

    public PageFile(string path, JetFormat format, FileMode mode)
        : this(path, format, mode, FileAccess.ReadWrite) { }

    /// <summary>
    /// Opens the underlying file. <paramref name="access"/> should be
    /// <see cref="FileAccess.Read"/> when the caller will not mutate pages
    /// (read-only access avoids contending with other readers for write locks
    /// on corpus files mounted from read-only filesystems or git checkouts).
    /// </summary>
    public PageFile(string path, JetFormat format, FileMode mode, FileAccess access)
    {
        _path   = path   ?? throw new ArgumentNullException(nameof(path));
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _stream = new FileStream(path, mode, access, FileShare.ReadWrite);
    }

    public byte[] ReadPage(int pageNumber)
    {
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        long offset = (long)pageNumber * _format.PageSize;
        if (offset + _format.PageSize > _stream.Length)
            throw new EndOfStreamException($"Page {pageNumber} is beyond end of file.");

        var buffer = new byte[_format.PageSize];
        ReadCount++;
        _stream.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < buffer.Length)
        {
            int n = _stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }

        if (read != buffer.Length)
            throw new EndOfStreamException($"Failed to read full page {pageNumber}.");

        return Codec.DecodePage(buffer, pageNumber);
    }

    /// <summary>
    /// As <see cref="ReadPage"/>, for a page every page appended consults (the header,
    /// the global usage map): read from the file once, then kept as written here. The
    /// copy returned is the caller's to change. Like the rest of this library, it does
    /// not see another process writing the file while it is open.
    /// </summary>
    internal byte[] ReadKeptPage(int pageNumber)
    {
        if (!_kept.TryGetValue(pageNumber, out var page))
        {
            page = ReadPage(pageNumber);
            _kept[pageNumber] = page;
        }
        return (byte[])page.Clone();
    }

    public void WritePage(int pageNumber, byte[] data)
    {
        if (pageNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        if (data.Length != _format.PageSize)
            throw new ArgumentException(
                $"Page data must be exactly {_format.PageSize} bytes for {Format.Version}.");

        byte[] encoded = Codec.EncodePage(data, pageNumber);
        long offset = (long)pageNumber * _format.PageSize;
        long requiredLength = offset + _format.PageSize;
        if (_stream.Length < requiredLength)
            _stream.SetLength(requiredLength);

        _stream.Seek(offset, SeekOrigin.Begin);
        _stream.Write(encoded, 0, encoded.Length);
        _stream.Flush();
        if (_kept.ContainsKey(pageNumber))
            _kept[pageNumber] = (byte[])data.Clone();
    }

    public void ReplaceWith(Stream source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        _kept.Clear();
        _stream.Seek(0, SeekOrigin.Begin);
        _stream.SetLength(0);
        source.CopyTo(_stream);
        _stream.Flush();
    }

    public string Path => _path;

    public void Dispose()
        => _stream.Dispose();
}
