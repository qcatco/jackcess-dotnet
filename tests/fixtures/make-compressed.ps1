param(
  [Parameter(Mandatory)][string]$Path,
  [int]$Rows = 400
)

$ErrorActionPreference = "Stop"   # a Jet DDL slip must stop, not repeat 400 times

if (Test-Path $Path) { [IO.File]::Delete($Path) }

# ADOX creates a Jet 4 .mdb; a long shared prefix on the indexed text column is what makes
# Access prefix-compress the index pages.
$cat = New-Object -ComObject ADOX.Catalog
$cat.Create("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=$Path;") | Out-Null

$conn = New-Object -ComObject ADODB.Connection
$conn.Open("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=$Path;")
$conn.Execute("CREATE TABLE Docs (Id LONG, DocNo TEXT(60), Descr TEXT(40), CONSTRAINT PK_Docs PRIMARY KEY (Id))") | Out-Null
$conn.Execute("CREATE INDEX IX_DocNo ON Docs (DocNo)") | Out-Null

for ($i = 1; $i -le $Rows; $i++) {
  $doc = "IR-1404-COMMISSION-DOCUMENT-{0:D8}" -f $i
  $conn.Execute("INSERT INTO Docs (Id, DocNo, Descr) VALUES ($i, '$doc', 'n$i')") | Out-Null
}

$rs = $conn.Execute("SELECT COUNT(*) FROM Docs")
Write-Host ("built {0} with {1} rows" -f (Split-Path $Path -Leaf), $rs.Fields.Item(0).Value)
$conn.Close()
