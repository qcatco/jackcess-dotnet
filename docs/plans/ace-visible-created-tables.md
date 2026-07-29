# As built: making library-created tables readable by Microsoft Access

- **Status:** Done, shipped in 2.2.0 (2026-07-29)
- **Was:** a plan, written 2026-07-28, whose leading hypothesis turned out to be wrong.
  Kept as a record of what the causes actually were, because the wrong guess is
  instructive and because the technique that found them is reusable.

## The symptom

A file from `Database.Create` + `CreateTable` + inserts read back perfectly through
this library, and Access could not use it. It failed in two stages, and fixing the
first only revealed the second:

1. `SELECT … FROM Foroush_Detail` → *"cannot find the input table or query"* — Access
   did not know the table existed.
2. After the catalog fixes: the table was listed by ADOX, but every query returned
   *"Not a valid bookmark"*, and `CompactDatabase` discarded the table rather than
   rebuild it.

Appending into an Access-authored file was never affected, which is why production
work (copy a template, append rows) was unaffected throughout.

## What the original plan guessed, and why it was wrong

The plan reasoned that because `ListTables` found the table while
`GetTable("MSysObjects").ReadAllRows()` did not, the catalog row must have landed on
a page missing from `MSysObjects`' owned-pages usage map — "the same defect class as
the 2.0.0 usage-map work, one level up".

That was wrong. `ForEachTableEntry` walks the catalog through exactly that usage map
and *did* find the row, so the page was registered all along. The row was
unreachable for an unrelated reason: it was absent from the index Access enumerates
through (see cause 3 below).

## What the causes actually were

1. **`MSysObjects.ParentId` was `0`.** It must be the id of the `Tables` container
   object, looked up rather than hardcoded (`FindObject(0x0F000000, "Tables")`).
2. **No `MSysACEs` rows and no `Owner` blob.** Access stamps every catalog object
   with an owner and expects access-control entries; Jackcess Java does both in
   `addToSystemCatalog` + `addToAccessControlEntries`.
3. **Multi-column index entries were not in Jet's format.** `EncodeCompositeKeyBytes`
   emitted one leading start-flag byte for the whole key instead of one per column.
   Self-consistent — the writer's own lookups used the same encoding, so composite
   primary keys round-tripped and every test passed — but unreadable by Access *and*
   by this library's own `IndexReader`. Fixing this is what made the table appear.
4. **Column headers lacked two fields.** The variable-length-table index must carry
   the running counter on every column (a fixed column holds the index the next
   variable column will take), and every non-Numeric column carries the
   general-legacy text sort order, LCID 1033.
5. **The usage-map page had the wrong page type.** A table's owned/free maps are rows
   of a **data** page (`0x01`). `0x05` belongs to the global usage map and to the
   bitmap pages a reference map points at. This single byte was the last barrier and
   the whole reason for *"Not a valid bookmark"*: Access followed the table
   definition's usage-map pointer to a page it could not parse, so it resolved no
   rows even though the table was listed and the data was intact.

## The technique worth reusing

Write the **same schema twice into one file** — once with the ACE engine
(`ADOX.Catalog.Create` plus `CREATE TABLE` DDL), once with this library — then
byte-diff the structures layer by layer: catalog row → TDEF header → column headers
→ data page → usage-map page. Two definitions of the same schema are the same length,
so a straight diff names the offending offsets, and the repeating stride exposes
per-column fields immediately.

Two lessons from how this went:

- **Getting a layer byte-identical and still failing is a result.** Once the TDEF
  matched Access's apart from row count and page numbers, the TDEF was excluded and
  attention moved to the next structure — which is where the bug was.
- **Self-consistency hides format bugs.** Causes 3 and the text-compression bug in
  the same release both round-tripped perfectly through this library's own reader.
  Any check that only writes and re-reads with this library will miss that entire
  class of defect; the verification has to be an external engine.

## Verification

`Microsoft.ACE.OLEDB.12.0` (with ADODB and ADOX; `DAO.DBEngine.120` also works, JRO
is not registered on 64-bit). Row counts alone are not sufficient — assert on a
Latin-1 string too, or the text-compression defect passes unnoticed.

Portable regression cover lives in `tests/…/AccessCompatibilityTests.cs`, which
asserts the byte-level invariants each fix established, since the ACE round-trip
itself cannot be automated everywhere. Those tests were confirmed to fail when the
defects are reintroduced.
