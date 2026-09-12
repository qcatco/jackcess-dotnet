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

They used to be resolved from `D:/Projects/jackcess-jackcess-5.0.0/...`, an
absolute path on one developer's machine. Everywhere else — including every CI
runner — that directory does not exist, the corpus theories produce zero cases,
and xUnit **fails** a `[Theory]` with no data rather than skipping it. So the
suite was simultaneously untested (no corpus) and red (empty theories) for
anyone but the original author.

Committing the files makes the suite deterministic: same fixtures, same result,
on any machine, with no network fetch and no setup step.

## Size

~82 MB in the working tree, ~11 MB of git objects (Access files are mostly zero
padding and compress ~8:1). A clone pays the 11 MB.

## Layout

`V1997`, `V2000`, `V2003` hold `.mdb`; `V2007`, `V2010`, `V2019` hold `.accdb`.
`TestCorpus.Root` (tests/JackcessDotNet.Tests/TestCorpus.cs) resolves this
directory and is the single place any test asks for it. Point the suite at a
different corpus — a fuller one, or a private set of customer files — with the
`JACKCESS_CORPUS_PATH` environment variable.
