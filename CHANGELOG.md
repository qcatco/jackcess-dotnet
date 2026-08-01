# Changelog

All notable changes to JackcessDotNet are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.0.0]

Where 2.2.0 made a table this library *creates* readable by Access, this release makes
appending to a table **Access already built** keep that table's indexes correct. Access
reaches rows through its indexes, so a row missing from one does not exist as far as
Access is concerned even though a page scan still returns it. Verified by appending to
files the ACE engine wrote and querying the results back through
`Microsoft.ACE.OLEDB.12.0` — seeks, ranges, ordered walks and aggregates.

### Fixed

- **A split index recorded its new root against the wrong index.** Access does not order
  a TDEF's index-data blocks to match its index slots — in `common1V2000.mdb` the primary
  key is the second slot but owns the first block, and one file can even point two logical
  indexes at a single block. Every per-index field was addressed by the index's position in
  `Table.Indexes` instead, so when a leaf split promoted a new root, the root-page field of
  a *different* index was overwritten: a table's primary key ended up pointing at the
  secondary index's tree, holding collated text where Access expected a 4-byte Long. Access
  could still count and aggregate — those walk the tree without comparing keys — but every
  seek through either index failed, which is what the refusal below was standing in for.
  `Index.IndexDataNumber` now carries the block, and nothing addresses a block by slot.
- **Two logical indexes sharing one tree got two entries.** Access points a foreign-key
  index and a primary key at the same index-data block when they cover the same columns.
  Maintaining them per slot inserted the same key twice into the one tree; each block is now
  maintained once.
- **Index entry counts were not maintained.** Access answers `COUNT(*)` and `MAX(…)` from an
  index rather than by scanning, and a count of zero against a tree that does hold entries
  makes it fail those with "Invalid argument" even though a seek through the same index
  works.
- **The row writer placed values where the reader no longer looked for them.** Reading was taught
  to take a column's storage class from its fixed-length flag rather than from its type; the writer
  went on deciding by type, so a Long that Access stores in the variable-length area — the foreign
  key of a complex column's flat table — was written at a fixed offset and read back from the
  variable area, as null. The encoder now takes the same flag and can emit a fixed-width type into
  the variable area, mirroring the branch the decoder gained. Every write test in this suite runs
  against Jet 4 `.mdb`, where no column disagrees with its type, which is how the two drifted apart.
- **Long-value pages were added to the table's own page list.** `LvalWriter` assumed its usage map
  was row 0 of its page, which holds for a table this library creates — each long-value column gets
  a page to itself — but Access keeps those maps as further rows of the table's *own* usage-map
  page, where row 0 is the table's owned-pages map. So writing a Memo added its pages to the
  table's page list, and a scan then read them as rows: this library counted one row more than
  Access did. The reference's row is now carried alongside its page, and the two counts agree.
- **Long-value usage-map references were read one field early.** Access precedes them with a
  2-byte count, which was being consumed as part of the first reference — turning row 2 of page 110
  into page 0x6E0200. Writing a Memo then read far past the end of the file. Reading one never
  noticed: that follows the reference held in the row itself and consults the map only to allocate.
  The count is taken only when it matches the number of long-value columns, so files written before
  this fix, which carry no count, still read.
- **A column's storage class was inferred from its type instead of read from its flag.** Whether a
  value sits at a fixed offset or in the row's variable-length area is the column's own
  fixed-length flag; Access stores some numeric columns as variable-length, and the foreign key of
  a complex column's flat table is a `Long` stored exactly that way. Reading those at a fixed
  offset returned whatever happened to be there — the link from an attachment to its row came back
  null, and its sibling column read a value belonging to neither. The row decoder also had no
  branch for a fixed-width type found in the variable area, and `ComplexColumns` preferred the
  table-qualified back-reference over the bare one, which agreed only while both were being read
  from the same wrong offset. With all three corrected, attachments resolve to exactly the counts
  Jackcess Java asserts for `complexDataV2007.accdb`.
- **Rows were threaded only into the primary-key index.** Appending 500 rows to an
  Access-authored table left Access reporting one row — its own — for anything that used
  another index.
- **Deleting a row touched no index at all, and updating one added a second entry rather than
  moving the first.** `DeleteRow` left an entry pointing at a slot that no longer holds what it
  claims and never decremented the index's entry count, so Access — which answers `COUNT(*)` from
  an index and follows its entries without rechecking the row — over-reported and could return a
  deleted row. `UpdateByPrimaryKey` inserted a fresh primary-key entry beside the stale one and
  ignored secondary indexes entirely, so a changed indexed value stayed indexed under its old key.
  Both now take the row's old entries out of every index and put the new ones in, counts included.
  Removing an entry also refreshes the ancestor keys it invalidates: a node entry holds the
  greatest key in its child's subtree, so dropping a leaf's greatest entry left the parent claiming
  a range the leaf no longer covered, and a search inside that gap descended into the wrong leaf
  and found nothing. Pages are not merged when they empty — an empty leaf keeps its place, which
  hides nothing, since every key at or below its stale parent key either lived there and is gone or
  lives further left.

  This was invisible to the library's own tests because `IndexCursor` filters entries whose row no
  longer matches, so every seek-based assertion passed against a file Access would read wrongly.
  The tests now assert leaf entry counts and the stored entry count off the page.

### Added

- **Creating secondary indexes** — `Database.CreateIndex(table, name, columns…)`, single-column
  or composite (up to Jet's 10), optionally flagged unique. A TDEF interleaves per-index with
  per-column data, so an index is not one record that could be appended: its row-count block
  sits before the column definitions, its column block and slot after the column names, and its
  name after the other index names. The new sections are spliced in, copying every existing byte
  through unchanged rather than re-deriving the definition — the exact bytes Access accepts were
  established one field at a time, and re-serialising would risk all of it. The tree is then
  filled from the rows already stored, because an index Access can see but that answers nothing
  is worse than no index. Access lists the result through ADOX and uses it for seeks, `ORDER BY`,
  `GROUP BY` and `MAX`. Two limits: the definition must still fit on one page, and `unique` is
  recorded for Access's benefit but not enforced by this library's inserts.
- **Creating indexes with descending columns** — `Database.CreateIndex` takes `IndexColumnSpec`
  (a column name plus a direction, implicitly convertible from a plain string, so existing calls
  are unchanged). Jet stores a descending column's key bytes inverted and clears the ascending bit
  in its flag byte; the writer only ever emitted the ascending form, so such an index could not be
  made. ADOX confirms Access reads the direction back.
- **Uniqueness enforced on insert** for every unique index, primary keys included. The promise was
  recorded and never checked, so a duplicate produced a file Access considers corrupt. A key with a
  null component is exempt, following SQL rather than Access.
- **Table definitions spanning several pages** are read and written (`TdefChain`). Only the first
  page used to be read, and a wide table's index sections fall past it — which was handled by
  reporting *no indexes at all*, so appending to such a table left every index untouched.
- **The Agile data-integrity hash** (MS-OFFCRYPTO §2.3.4.14) — `AgileDataIntegrity` computes and
  verifies the `encryptedHmacKey` / `encryptedHmacValue` pair, and `OfficeCryptCodecHandler`
  exposes `HasDataIntegrity`, `VerifyDataIntegrity` and `ComputeDataIntegrity` for an
  Agile-encrypted file. **Round-trip tested only:** tampered content, a wrong key value and swapped
  ciphertexts all fail the check and every Agile hash algorithm round-trips, but nothing here
  compares the result with Microsoft's, which needs a file that already carries the element or real
  Access to open one written here. Which bytes the hash covers is left to the caller — the
  specification defines it over an OOXML package's encrypted stream and an Access database has
  none.
- **`Table.AddComplexValue`** — appends one value to a complex column, filling both link columns:
  the foreign key back to the owning row and the flat row's own sequential id, which Access numbers
  across the whole flat table rather than per owning row. Works for multi-value fields and
  attachments — the ACE engine reads both back from a file this library wrote into. Writing a Memo
  or OLE value into an `.accdb` is not there yet: it completes and round-trips here, but Access
  reads it back as empty — though the page accounting around it is now correct.
- **`PageFile.PagesRead`** — page reads are what an operation costs, and no correctness test can
  tell a seek from a scan.
- **Reading complex columns** — multi-value fields, attachments and append-only memo history, via
  `Table.GetComplexValues(row, columnName)`. The row stores a 4-byte id; `MSysComplexColumns` maps
  the column to its flat table, and the returned rows are that table's, so the shape follows the
  kind of complex column.
- **`Index.IndexDataNumber`** — which index-data block in the TDEF holds an index's tree.

### Performance

- **Inserting no longer reads the whole table.** Finding a page with room walked the owned-pages
  map — every page the table has ever used — so loading n rows cost O(n²) page reads. The
  free-space map, which the table already carried and this library never wrote to, is used and
  maintained instead: 4000 rows now cost about nine page reads each.
- **Deleting and updating seek instead of scanning**, through a single-column index on the column
  when there is one, and act on the row pointer they find rather than searching again.
- **Deleted space is reclaimed, on emptied pages and within pages still in use.** A page whose
  every row is deleted is reset wholesale; a page that still holds live rows has their bytes packed
  back together, which is what frees the space a flagged row was sitting on. Slots keep their
  numbers through the move — an index entry points at `(page << 16) | slot`, so renumbering would
  leave every index pointing at the wrong rows, which is why Access reclaims this only during a
  compact-and-repair where it rebuilds the indexes too. Compaction happens when page selection is
  about to give up on a candidate, so it costs a rewrite only when it saves an allocation.

### Changed

- **Nothing about growing an index is refused any more.** Three cases used to throw rather than
  risk an index, and all three are now written and verified against the ACE engine:
  - **a leaf split**, in a tree this library grew and in one Access wrote;
  - **a page Access prefix-compressed** — the shared prefix is put back and the page re-emitted in
    full with a zeroed prefix count. This was refused on a measurement taken while the index-block
    bug above was cross-wiring roots; with that fixed, inserting keys *interleaved* among 400
    existing ones across four compressed leaves left every old and new key seekable through ACE.
    (Keys appended above the existing range prove nothing here — they all land on the one
    uncompressed tail leaf without expanding a prefix.)
  - **a full node**, which now splits in two under a new level, so a tree grows past two levels.
    A node covers children `c0..cn` as an entry each except the last plus a child-tail pointer;
    splitting at entry `m` keeps `c0..cm` (with `cm` as the new tail) and moves `c(m+1)..cn` to a
    new page, entry `m` becoming the separator the parent records. Insertion records the nodes it
    descends through and walks a split back up them, so any depth is handled.

  A `Table.ForceIgnoreIndexCheck` / `ImportOptions.ForceIgnoreIndexCheck` pair was added partway
  through this release to opt out of the refusal, and is **gone again** — with all three cases
  written there was nothing left for it to suppress. It was never in a published package, so no
  caller can be relying on it. The only refusal left is a key too large for three to share a page,
  which is what splitting a node needs; Jet's 255-byte key limit puts that out of reach.
- **Breaking:** `IndexWriter.InsertIntoIndex`, `WouldExceedIndexCapacity` and
  `IncrementIndexRowCount` take an `Index` rather than an `int` ordinal, so a slot position
  can no longer be passed where a block number belongs. `IncrementIndexRowCountForDataBlock`
  covers the one case with no `Index` to hand: a table created in the current session, whose
  TDEF blocks have not been read back yet.

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
