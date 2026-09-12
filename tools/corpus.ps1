#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Verify (or regenerate) the checksum manifest for tests/corpus.

.DESCRIPTION
    The Access fixtures under tests/corpus are the ground truth this parser is
    checked against, so "which bytes did we test against?" has to be answerable
    from a build log alone. This script hashes every fixture and compares it to
    tests/corpus/MANIFEST.sha256.

    It catches what a file-count check cannot: a truncated checkout, a fixture
    quietly edited in place, a .mdb mangled by a CRLF filter, or a partial LFS
    /cache restore. Any of those otherwise surface as a baffling parser failure
    a long way from the cause.

    The corpus is APPEND-ONLY. Adding a fixture is normal; changing one is not.
    -Update exists for adding files, not for making a red build green - if a
    hash changed and you did not deliberately add or replace that fixture, the
    checkout is wrong, not the manifest.

.EXAMPLE
    pwsh tools/corpus.ps1            # verify (exit 1 on any mismatch)
    pwsh tools/corpus.ps1 -Update    # regenerate after ADDING fixtures
#>
[CmdletBinding()]
param([switch]$Update)

$ErrorActionPreference = 'Stop'
$repo     = Split-Path $PSScriptRoot -Parent
$corpus   = Join-Path $repo 'tests/corpus'
$manifest = Join-Path $corpus 'MANIFEST.sha256'

if (-not (Test-Path $corpus)) { throw "No corpus at $corpus" }

# Binary fixtures only - the README/LICENSE/manifest beside them are text and
# are reviewed as text, not pinned by hash.
$files = Get-ChildItem $corpus -Recurse -File -Include *.mdb, *.accdb |
         Sort-Object { $_.FullName.Substring($corpus.Length + 1).Replace('\', '/') }

if ($files.Count -eq 0) { throw "No .mdb/.accdb fixtures found under $corpus" }

$actual = [ordered]@{}
foreach ($f in $files) {
    $rel = $f.FullName.Substring($corpus.Length + 1).Replace('\', '/')
    $actual[$rel] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
}

if ($Update) {
    # sha256sum-compatible: two spaces, then the path. Lets `sha256sum -c` work
    # from git-bash/WSL as well as this script.
    $lines = $actual.GetEnumerator() | ForEach-Object { "$($_.Value)  $($_.Key)" }
    Set-Content $manifest ($lines -join "`n") -NoNewline -Encoding ascii
    Add-Content $manifest "`n" -NoNewline -Encoding ascii
    "Wrote {0} entries to {1}" -f $actual.Count, $manifest
    return
}

if (-not (Test-Path $manifest)) { throw "Manifest missing: $manifest (run with -Update)" }

$expected = [ordered]@{}
foreach ($line in Get-Content $manifest) {
    if ($line -match '^\s*$') { continue }
    $hash, $path = $line -split '\s\s', 2
    $expected[$path.Trim()] = $hash.Trim().ToLower()
}

$missing   = @($expected.Keys | Where-Object { -not $actual.Contains($_) })
$untracked = @($actual.Keys   | Where-Object { -not $expected.Contains($_) })
$changed   = @($expected.Keys | Where-Object { $actual.Contains($_) -and $actual[$_] -ne $expected[$_] })

$totalMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
"corpus: {0} fixtures, {1} MB, manifest has {2} entries" -f $actual.Count, $totalMb, $expected.Count

foreach ($p in $missing)   { Write-Host "  MISSING   $p" -ForegroundColor Red }
foreach ($p in $changed)   { Write-Host "  CHANGED   $p (expected $($expected[$p]), got $($actual[$p]))" -ForegroundColor Red }
foreach ($p in $untracked) { Write-Host "  UNTRACKED $p - run tools/corpus.ps1 -Update if this fixture was added deliberately" -ForegroundColor Yellow }

if ($missing.Count -or $changed.Count) {
    throw "Corpus verification FAILED: $($missing.Count) missing, $($changed.Count) changed. See tests/corpus/README.md."
}
if ($untracked.Count) { throw "Corpus has $($untracked.Count) fixture(s) not in the manifest." }

"corpus OK - all $($actual.Count) fixtures match the manifest"
