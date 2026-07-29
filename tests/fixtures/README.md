# Test fixtures

Files here are checked in because they cannot be produced by this library.

## `ace_compressed_index.mdb`

A Jet 4 `.mdb` written by the ACE engine (`Microsoft.ACE.OLEDB.12.0`), holding `Docs` with 400
rows, a `Long` primary key (`PK_Docs`) and a secondary text index (`IX_DocNo`) whose values all
share a 31-character prefix — which is what makes Access **prefix-compress** the index leaves.

That is the point of the file: this library always writes index pages with a zero prefix count, so
it can never generate a compressed page to test against. Four of `IX_DocNo`'s five leaves are
compressed (`prefix=31`), so an insert has to expand those entries and re-emit them in full.

Rebuild it with `make-compressed.ps1` (needs ACE registered on the machine):

```powershell
pwsh ./make-compressed.ps1 -Path .\ace_compressed_index.mdb -Rows 400
```

Note that `Note` cannot be used as a column name in Jet DDL — it is a type alias, and
`CREATE TABLE` fails with "Syntax error in field definition".
