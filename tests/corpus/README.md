# Test corpus

Real Access database files used as reality-check fixtures by the test suite.

## Provenance

Copied verbatim from the Java **Jackcess** project's test resources
(`src/test/resources/data` in the jackcess 5.x source tree — the reference
implementation this library is a port of). They are redistributed here under
the Apache License 2.0; the full licence text is in `LICENSE.txt` beside this
file. No file has been modified — the bytes are what upstream ships, which is
the whole point: they are the ground truth a format parser is checked against.

## Why these live in the repo

They were previously resolved from an absolute path outside the repository, so
on any machine without that exact directory — every CI runner included — the
corpus theories produced zero cases. xUnit **fails** a `[Theory]` with no data
rather than skipping it, which left the suite both unchecked against real
Access files and red, at the same time, for the same reason.

Committing the files makes the suite deterministic: same fixtures, same result,
on any machine, with no network fetch and no setup step.

## Integrity — MANIFEST.sha256

Every fixture is pinned by SHA-256 in `MANIFEST.sha256`, checked by
`tools/corpus.ps1` as the first step of the workflow. It catches what a file
count cannot: a truncated checkout, a fixture edited in place, a `.mdb` mangled
by a newline filter, a partial cache restore. Without it those land as a
confusing parser failure far from the cause.

The manifest is `sha256sum`-compatible, so `sha256sum -c MANIFEST.sha256` from
this directory works too.

**The corpus is append-only.** Adding a fixture is routine — add it, run
`pwsh tools/corpus.ps1 -Update`, commit both. Changing an existing one is not:
if a hash moved and you did not deliberately replace that file, your checkout is
wrong and the manifest is right. Never run `-Update` to turn a red build green.

These 82 files are byte-identical to the upstream Java corpus (verified by
running `sha256sum -c` against an independent checkout).

## Size

~80 MB in the working tree, ~11 MB of git objects (Access files are mostly zero
padding and compress ~8:1). A clone pays the 11 MB; CI checkout of the whole
thing measured **6 seconds**, against 42s for `setup-dotnet` in the same run.

Plain git is the right home for them precisely because they never change: git's
cost is driven by churn, not size, so an immutable blob is paid for once. Git
LFS would be a downgrade here — it stores the 80 MB uncompressed, bills
bandwidth on a public repo, and needs `lfs: true` on every checkout.

## Layout

`V1997`, `V2000`, `V2003` hold `.mdb`; `V2007`, `V2010`, `V2019` hold `.accdb`.
`TestCorpus.Root` (tests/JackcessDotNet.Tests/TestCorpus.cs) resolves this
directory and is the single place any test asks for it. Point the suite at a
different corpus — a fuller one, or a private set of customer files — with the
`JACKCESS_CORPUS_PATH` environment variable.
