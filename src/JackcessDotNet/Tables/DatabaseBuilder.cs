namespace JackcessDotNet;

/// <summary>
/// Fluent factory for <see cref="Database"/>. Mirrors Java Jackcess's <c>DatabaseBuilder</c>.
///
/// Usage:
/// <code>
///   using var db = new DatabaseBuilder()
///       .WithFile("MyDb.mdb")
///       .WithVersion(JetVersion.Jet4)
///       .Create();
///
///   using var db2 = DatabaseBuilder.Open("Existing.mdb");
/// </code>
/// </summary>
public sealed class DatabaseBuilder
{
    private string?    _path;
    private JetVersion _version = JetVersion.Jet4;

    /// <summary>Sets the file path. Required before <see cref="Create"/>.</summary>
    public DatabaseBuilder WithFile(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        return this;
    }

    /// <summary>Selects the on-disk format to create. Defaults to Jet4 (.mdb / Access 2000-2003).</summary>
    public DatabaseBuilder WithVersion(JetVersion version)
    {
        _version = version;
        return this;
    }

    /// <summary>Creates a new database at the configured path.</summary>
    public Database Create()
    {
        // The null test is spelled out separately rather than left to
        // IsNullOrWhiteSpace: net472's reference assemblies lack the
        // [NotNullWhen(false)] annotation on it, so flow analysis cannot see
        // _path as non-null afterwards and the net472 leg warns CS8604. An
        // explicit `is null` narrows on both targets without a suppression.
        if (_path is null || string.IsNullOrWhiteSpace(_path))
            throw new InvalidOperationException("DatabaseBuilder.Create requires a file path (call WithFile).");
        return Database.Create(_path, _version);
    }

    /// <summary>Opens an existing database, auto-detecting the format from the file header.</summary>
    public static Database Open(string path) => Database.Open(path);

    /// <summary>Opens an existing database with the specified version.</summary>
    public static Database Open(string path, JetVersion version) => Database.Open(path, version);
}
