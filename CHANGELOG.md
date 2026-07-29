# Changelog

All notable changes to JackcessDotNet are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.2.0]

Everything here is one theme: **files this library writes are now readable by
Microsoft Access.** Each item was found by writing the same schema twice into one
file — once with the ACE engine, once with this library — and byte-diffing the two,
because in every case the library round-tripped its own output perfectly. Verified
by reading the results back through `Microsoft.ACE.OLEDB.12.0`.

### Fixed

- **Text was written compressed into columns that don't allow it.** `EncodeText`
  compressed any string whose chars all fit Latin-1, ignoring the target column's
  compressed-unicode flag, so Access — which only looks for the `0xFF 0xFE` header
  on a flagged column — read the value as UTF-16 and paired the bytes:
  `ASKARISHAHI` came back as `十䅋䥒䡓䡁`, `1000000001` as `〱〰〰〰㄰`. Persian text was
  unaffected (it can't compress), which is how this survived so long. Compression
  now requires the column's flag plus Jet's own conditions (>2 chars, every char in
  `1..0xFF`); `Column.IsCompressedUnicode` is parsed from the column's ext-flags
  byte and written back, and `ColumnBuilder.CompressedUnicode()` opts a new column
  in. Uncompressed is the default, since it is readable either way.
- **A table created by this library was invisible to Access.** Its `MSysObjects`
  row was parented to `0`; it now resolves the `Tables` container object, carries
  that container's `Owner` blob, and gets `MSysACEs` entries — mirroring Jackcess
  Java's `addToSystemCatalog` + `addToAccessControlEntries`.
- **Multi-column index entries were not in Jet's format.** They were framed with a
  single leading start-flag byte instead of one per column
  (`[flag][col0][flag][col1]`). That was self-consistent — the writer's own lookups
  encoded search keys the same way, so composite primary keys round-tripped and the
  tests passed — but no Access could find such an entry, which is why an appended
  `MSysObjects` row never showed up as a table. A null column now collapses to a
  lone null-flag byte, as the reader already expected.
- **Column headers were missing two fields Access relies on.** The
  variable-length-table index carries the *running* counter on every column (a
  fixed column stores the index the next variable column will take), and every
  non-Numeric column carries the general-legacy text sort order (LCID 1033);
  precision and scale remain for Numeric. The generated table definition is now
  byte-identical to Access's for the same schema apart from row count and page
  numbers.
- **A table's usage-map page had the wrong page type.** Access keeps the owned and
  free maps as rows of a **data** page (`0x01`); `0x05` is only for the global usage
  map and for the bitmap pages a reference map points at. Writing `0x05` left
  Access following the table definition's usage-map pointer to a page it could not
  parse, so it resolved no rows at all and reported *"Not a valid bookmark"* — even
  though the table was listed and every row was intact.

### Added

- `ColumnBuilder.CompressedUnicode()`, `Column.IsCompressedUnicode`, and
  `IndexWriter.InsertIntoIndexAtRoot` (inserts into a non-primary index by root
  page, so a system table's indexes stay in step when a row is appended).

## [2.0.0]

### Changed — breaking

- **Targets `net10.0`; `net8.0` is dropped.** The library is pure managed code and
  needed nothing from .NET 10 to work, so this is a platform decision rather than a
  technical one — it aligns the package with the .NET 10-only solutions that consume
  it. `1.2.2` remains the last `net8.0` release. This is the only breaking change in
  2.0.0: no API was removed or altered, so moving a `net10.0` project from 1.2.x to
  2.0.0 needs no code changes.
- **No NuGet dependencies left.** `System.Text.Encoding.CodePages` (the legacy Windows
  code pages Jet stores text in) and SourceLink both ship in the .NET 10 SDK and shared
  framework, so both `PackageReference` items are gone — referencing them now earns
  NU1510. Nothing about the library's behaviour changes.

### Added

- **Reference-style usage maps are now written, not just read.** When a table's
  owned pages outgrow what one inline bitmap can span, the map is promoted to a
  reference map in place — the row keeps its slot, and its start-page + bitmap
  bytes become 4-byte pointers to dedicated bitmap pages (Jackcess Java's
  `promoteInlineHandlerToReferenceHandler`). For the 69-byte rows
  Access-authored files use that lifts a table's reach from 512 pages to
  **556,512 pages (~2.2 GB)** — past Access's own file-size limit, so the
  ceiling stops mattering. Existing files' reference maps were already readable;
  the two paths are now symmetric.
- `UsageMap.CanAddPage` (with an `out string? refusal` overload) — asks whether a
  page can be recorded without mutating anything, and why not if it can't.
- `PageAllocator.NextPageNumber` and `PageAllocator.AllocateReferenceBitmapPage`.

### Fixed

- **Appending to a table past its usage map's window.** A table's owned-pages
  usage map addresses `bitmapLen * 8` pages from a fixed start page — as few as
  512 pages (~2 MB) in Access-authored files, versus the 1600 a file created
  here gets. Any insert needing a page outside that window threw
  `NotSupportedException: Page N is beyond the inline bitmap capacity`, so
  appending to a real-world `.mdb` failed almost immediately. `UsageMap.AddPage`
  now slides the window to cover the whole owned span first, as Jackcess Java's
  `InlineHandler.addOrRemovePageNumberOutsideRange` does. A table therefore gets
  its bitmap's full worth of pages wherever those pages sit in the file —
  512 pages of table data for a 64-byte bitmap — and beyond that the map is
  promoted to reference-style (see Added).
- **Orphan page on every failed insert.** `FindOrAllocateDataPage` (and
  `LvalStore`) allocated a page before registering it, and `PageAllocator`
  extends the file the moment it hands one out — so an insert that failed to
  record the page left 4 KB stranded, permanently. Callers that log-and-continue
  turned a hard stop at ~2 MB into an ever-growing file. Registration now
  happens first, against `PageAllocator.NextPageNumber`, so a refusal costs
  nothing. Added `UsageMap.CanAddPage` for callers that want to ask without
  mutating.

## [1.2.2]

### Added

- **Package icon.** `jackcess.png` (256×256, ~48 KB) wired via
  `<PackageIcon>` so the nuget.org listing renders an icon next to the
  package title.

No library code changes vs. 1.2.1.

## [1.2.1]

### Changed

- **Package metadata.** Added `PackageProjectUrl`, `RepositoryUrl`, and
  `RepositoryType` so the nuget.org package page surfaces the GitHub repo
  under "Project website" and "Source repository".
- **SourceLink.** Added `Microsoft.SourceLink.GitHub` (PrivateAssets="All").
  Consumers can now debug-step from the JackcessDotNet symbols into the
  exact source on GitHub matching the published commit SHA.
- **Deterministic builds in CI.** `ContinuousIntegrationBuild` enabled when
  `GITHUB_ACTIONS=true` so the .nupkg shipped from CI is reproducible.
- **README badges.** Added NuGet (version + downloads), GitHub (author + repo),
  and license badges visible on both GitHub and the nuget.org package page.

No library code changes vs. 1.2.0 — same `.dll` byte-for-byte aside from
embedded SourceLink metadata.

## [1.2.0]

### Added

- **Office Agile Encryption write support** (Office 2010+ `.accdb`). Pages
  encrypt on write using the same AES-CBC + per-page IV scheme as the read
  path, so round-trips through `Database.Open(path, password)` are lossless.
  Note: the DataIntegrity HMAC isn't recomputed — Office Access may flag a
  stale integrity hash on modified files. Matches the upstream `jackcess-encrypt`
  limitation.
- **ECMA Standard Encryption write support** (Office 2007 `.accdb`). AES-ECB
  encrypt on the same per-page key-derivation path as decrypt.
- **RC4 CryptoAPI Encryption** (Office 2002–2003 `.accdb`, `vMinor=2` with
  `FAES_FLAG` clear and `algId=0x6801`). Read + write — RC4 is symmetric.
  Per MS-OFFCRYPTO §2.3.5.2, including the 40-bit-key → 128-bit padding quirk.
- **Non-Standard AES encryption** (compat mode 0). Activates when a file
  advertises RC4 in the flags but actually contains AES in the algorithm ID.
  Falls back automatically with hash iterations set to 0.
- **Multi-column primary keys** via `Database.CreateTable(name, columns,
  primaryKeyColumns: new[] { "A", "B" })`. Up to 10 columns per composite key
  (Jet's hard limit). The composite key encoder writes a single leading
  ascending-flag byte then concatenates per-column value bytes in declared
  order.
- **Foreign-key enforcement on insert** (opt-in). Set
  `Database.EnforceForeignKeys = true`, and `Table.Insert` validates that each
  FK column value references an existing row in the parent table per
  `MSysRelationships`. Restrict-only — no cascade. Null FK values pass
  (SQL semantics).
- **`ImportOptions.AppendIfExists`** for the wrapper. When set, importing to
  a name that already exists in the database appends rows into the existing
  schema instead of throwing. Source columns/properties are matched to target
  columns by name (case-insensitive); unmatched source columns are dropped
  silently, unmatched target columns are left null.
- **`OfficeCryptCodecHandler.Scheme`** accessor — string label that names the
  active scheme (e.g. `"Agile Encryption (Office 2010+)"`,
  `"RC4 CryptoAPI Encryption (RC4-128)"`). Useful for diagnostics.
- **CHANGELOG.md** shipped inside the `.nupkg`.

### Fixed

- **`.accdb` page decryption was producing garbage** for every Office Crypto
  variant. The encoding key at header offset `0x3E` is XOR-masked against
  `BASE_HEADER_MASK` at rest (Jackcess Java applies this de-obfuscation at
  the PageChannel layer, but this port has no such layer). The codec now
  un-masks the encoding key before deriving per-page IVs / per-page RC4 keys.
- **Unencrypted `.accdb` files were rejected** when the bytes at offset
  `0x3E` happened to contain random non-zero data (e.g. the `linkeeTest.accdb`
  fixture from the upstream corpus). The "blank key" check now runs against
  the *un-masked* bytes and short-circuits when the `EncryptionInfo.version`
  field is `0.0`.
- **NonStandard AES dispatch escape**. `BuildStandardOrRc4` previously called
  `StandardEncryptionInfo.Read` *before* dispatching to the RC4-or-NonStandard
  fallback, so a `NotSupportedException` thrown by `ValidateRc4Header`
  escaped past the try/catch ladder. The Read is now deferred into the
  individual sub-paths, each with its own validation.
- **`Database.GetTable` no longer returns `null` PK metadata for composite
  PKs**. The reader rebuilds `PrimaryKeyColumnNames` from the on-disk index
  column block when more than one column is registered.

### Changed

- `OfficeCryptCodecHandler.HashBytes` switched from `HashAlgorithm.TransformBlock`
  + `TransformFinalBlock` to .NET 8's static one-shot methods
  (`SHA512.HashData` etc.). ~10× faster on the 100,000-iteration spin loop
  and removes a class of `HashAlgorithm` state subtleties.
- `OfficeCryptCodecHandler.AgileDescriptor.KeyData` (nested type) renamed to
  `KeyParams` to resolve CS0102 ("property and nested type can't share a name").
  No external behavior change — the type is internal.

## [1.1.0]

### Added

- **Office Crypto codec for `.accdb`** — read-only support for:
  - **Agile Encryption** (Office 2010+, `vMajor=4, vMinor=4`). MS-OFFCRYPTO
    §2.3.4.10–13 with PBKDF + AES-CBC and configurable hash. Verifies the
    password before returning a codec; throws `UnauthorizedAccessException`
    on mismatch rather than feeding garbage to the row parser.
  - **ECMA Standard Encryption** (Office 2007, `vMajor∈{2,3,4} / vMinor=2`).
    MS-OFFCRYPTO §2.3.4.5–9 with 50,000-iteration SHA-1 derivation and
    AES-ECB page crypto.

## [1.0.0]

### Added

- Initial release.
- **Read** Jet 3 (Access 97) and Jet 4 (Access 2000–2003) `.mdb` files.
- **Read** ACE 12 / 14 / 16 / 17 (`.accdb`, Access 2007 through 2019).
- **Create** new `.mdb` files at Jet 4 (and Jet 3 — read path only on Jet 3
  schema, write path on Jet 4).
- **Row CRUD**: `Insert`, `UpdateByPrimaryKey`, `DeleteRow`, table-scan cursor,
  index cursor with `FindRowByPrimaryKey` for single-column PKs.
- **B-tree primary-key index** with split-and-promote: root starts as a leaf,
  flips to a 2-level tree on overflow.
- **Memo / OLE long-value** storage via LVAL pages.
- **PropertyMap** reader (table-level + per-column properties from
  `MSysObjects.LvProp`).
- **MSysRelationships** parser exposed via `Database.GetRelationships()`.
- **Password-protected `.mdb`** via the Jet RC4 codec (Access 97 / 2000–2003).
- **`DatabaseImporter`** wrapper: `ImportTable(DataTable)`, `ImportTables(DataSet)`,
  and `ImportTable<T>(IEnumerable<T>)`. Schema inferred from source type;
  POCO reflection honours `[Key]`, `[Column]`, `[MaxLength]`, `[NotMapped]`,
  `[DatabaseGenerated(Identity)]`. `ImportOptions.FallbackUnmappableToString`
  stores unmappable CLR types as Memo via `ToString()`.
- **`DatabaseExporter`** wrapper: `ExportToDataTable(name)`, `ExportToDataSet()`,
  and lazy `ExportToCollection<T>(name?)`.
- Folder-organized source layout (`Codecs/`, `Cursors/`, `Indexes/`, `IO/`,
  `Pages/`, `Properties/`, `Schema/`, `Tables/`, `Util/`).
- NuGet package generated on every build (`GeneratePackageOnBuild`); README
  and LICENSE shipped inside the `.nupkg`; symbols emitted as `.snupkg`.
- GitHub Actions workflow that builds + tests on every push, and pushes to
  nuget.org on `v*` tags.
