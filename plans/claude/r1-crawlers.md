# R1 crawlers — fetch the raw artifacts (DEE2 archive + paper)

> **Status: DONE (2026-08-20).** The original acceptance criteria are implemented. This document is retained as an as-built historical record; future evolution is governed by [the active implementation plan](../implementation.md).

> The ArcIR export plan (`plans/claude/arcir-export-readiness.md`) defines **R1** — the
> *un/weakly structured* AI readiness level format — as the DEE2 count archive verbatim plus the paper,
> with the source *obscured* (no INSDC XML). Like R2, **R1 needs no IR at all**: it just needs two raw
> source artifacts on disk. This plan covers the crawl that fetches them.
>
> **As-built (2026-07-13).** Implemented in `BioFSharp.INSDC.Crawler`; the API below is the shipped
> surface, not a proposal.

## Context

R1 was originally sketched as *a prose description + a re-headered count matrix + the paper*, with
**R1A = paper.pdf** and **R1B = paper JATS**, plus an R1C variant that kept run accessions in the count
header. That definition is superseded. R1A/B now carry:

- the **DEE2 archive**, verbatim (the zip exactly as DEE2 serves it)
- the **paper** — **R1A = JATS XML, R1B = PDF**

Two consequences:

1. **The A/B assignment is inverted** relative to the old sketch (which had A=pdf, B=jats). This is
   deliberate.
2. **R1C is dropped.** Its only distinguishing feature was retaining run-accession headers — and shipping
   the archive verbatim retains them by definition, so the variant no longer distinguishes anything.

Because the archive ships as-is, R1 needs no header rewriting, no project slug, and no `titleAbbrev` or
`PaperSource` — the whole format is two files the crawler downloads. This is the same realization
`r2-crawlers.md` reached for R2: the readiness format is materialized entirely by the crawler.

Both artifacts already had crawlers (`Dee2.crawlDee2WithAsync`, `Paper`), so R1 is mostly *wiring* — with
one real gap, below.

## Settled decisions (clarifying session 2026-07-13)

| Decision | Resolution |
|---|---|
| **`description.txt`** | **Dropped.** R1A/B are exactly the DEE2 archive + the paper. The paper carries the prose context, so a separate description file is redundant. |
| **Output shape** | **One call, one readiness format, one tree.** `crawlR1 <format> …` writes a single tree holding the archive + that format's paper. Matches the orphan-branch-per-format flow in `playground/crawl_r1.fsx`. |
| **DEE2 archive** | **Verbatim zip** — `counts/<StudyAccession>.zip`, exactly what `Dee2.crawlDee2` already writes. Not extracted. |
| **INSDC XML** | **Never written.** R1 obscures its source. Records are still *fetched* (see "Lean fetch"), but no XML lands on disk. |
| **Paper format** | Fetched in the requested format **only** — never falling back to the other. A JATS miss yields `NotFound`, not a PDF. |

### The PDF does not come from EuropePMC (found 2026-07-13)

R1B needs an article PDF, and the original design fetched it from EuropePMC's `fullTextPDF` path.
**That endpoint 404s for every article** — including ones whose `fullTextXML` serves fine and which
EuropePMC itself flags open access. Its advertised browser route
(`europepmc.org/articles/<id>?pdf=render`) 404s too. So `PaperResult.Pdf` was **unreachable in
production**, and R1B could never have been produced — while every offline test passed, because the
stub simply handed back fabricated PDF bytes. A wholly fictitious URL would have passed them all.

PDFs now come from the **PMC Open Access dataset on AWS** (`pmc-oa-opendata`, us-east-1, public, no
auth), keyed by *versioned* PMCID:

```text
https://pmc-oa-opendata.s3.amazonaws.com/PMC7430643.1/PMC7430643.1.pdf
```

Verified against four PMCIDs, including two that no other route served (one an author-manuscript
deposit with no OA-subset PDF link at all). NCBI's OA service *does* hand out PDF links, but they
point into the legacy FTP tree that NCBI moved under `deprecated/` and **deletes in August 2026** —
so it is not a durable base to build on. `Paper.PmcOaVersions` walks `1; 2; 3`, and only on a miss,
so the common case is still a single request.

The moral, and the reason this went unnoticed: **an offline test against a made-up fixture cannot
tell you the endpoint exists.** A `LIVE`-gated test now downloads a real PDF and asserts its `%PDF-`
magic.

### The one real gap it closed

`Paper.crawlPaperWithAsync` hardwires *JATS first, PDF only as fallback*, so it could not be told to fetch
one specific format. That is fine for R2 (either format will do) but wrong for R1: **an R1A that silently
fell back to a PDF would in fact be an R1B.** Hence `crawlPaperFormatWithAsync` (below). The fallback
surface is unchanged and still what R2 uses.

## On-disk layout

```text
<outDir>/
  counts/<StudyAccession>.zip     # the DEE2 archive, verbatim (e.g. DRP003416.zip)
  paper/<PMCID>.jats.xml          # R1A only
  paper/<PMCID>.pdf               # R1B only
```

No `samples/`, `experiments/`, `runs/`, and no `<accession>.xml` at the root — that tree is R2's.

## Public API surface

> **Layering (revised 2026-07-13).** The crawler is three layers, and R1/R2 are only the top one. The
> middle — *resolve an INSDC accession into the ids the other services key on* — is public, because
> EuropePMC keys on a PMCID and DEE2 keys on an SRA study accession and **only the INSDC records know
> either**. Keeping that hop inside the orchestrators meant every new combination of (have-the-id /
> discover-the-id) × (paper / DEE2 / XML) needed a new orchestrator. It doesn't any more.

### `Crawler.fs` — the resolve layer

```fsharp
/// The records a paper/DEE2 lookup actually consumes — resolved once, reusable.
type InsdcRefs = { Accession: string; BioProjects: BioProject[]; Studies: Study[]; Discovered: DiscoveredSet }

/// Fetch ONLY the BioProject + Study. Not the BioSample/Experiment/Run records:
/// no paper or DEE2 lookup ever reads one, and on a big project they are thousands
/// of wasted requests. (`crawlAndDiscover` when you truly need the full set.)
val resolveWithAsync : CrawlOptions -> accession:string -> Async<InsdcRefs>
val resolveAsync / resolve

/// The same refs from records you already fetched — so the projections below work
/// whichever way you got them (lean `resolve`, or a full `crawl`).
val refsOf : accession:string -> CrawlResult -> DiscoveredSet -> InsdcRefs

/// The INSDC -> EuropePMC hop. Pure. Feed to `Paper.resolvePmcid`.
val paperRefs : InsdcRefs -> PublicationRef list

/// The INSDC -> DEE2 hop. Pure. The archive-assigned study Accession (SRP/ERP/DRP),
/// never the submitter Alias. This rule used to be an inline `Array.tryHead`.
val dee2Key : InsdcRefs -> string option
```

Which makes each of the four things you actually want a one-liner, with no orchestrator involved:

```fsharp
// paper, PMCID in hand
Paper.crawlPaperFormat PaperFormat.Jats "PMC123456" outDir
// paper, discovered from an accession
Crawler.resolve "PRJNA123" |> Crawler.paperRefs |> Paper.resolvePmcid
// DEE2, SRP in hand
Dee2.crawlDee2 "athaliana" "SRP183179" outDir
// DEE2, discovered from an accession
Crawler.resolve "PRJNA123" |> Crawler.dee2Key
```

### `Paper.fs` (additions)

```fsharp
/// Which EuropePMC full-text format to fetch. Qualified access is required
/// because `Pdf` would otherwise shadow `PaperResult.Pdf`.
[<RequireQualifiedAccess>]
type PaperFormat =
    | Jats
    | Pdf

/// Fetch the full text of `id` (a PMCID) in exactly `format` — no fallback to
/// the other format. `NotFound` when that format is unavailable.
val crawlPaperFormatWithAsync : CrawlOptions -> PaperFormat -> id:string -> outDir:string -> Async<PaperResult>
val crawlPaperFormatAsync     : PaperFormat -> id:string -> outDir:string -> Async<PaperResult>
val crawlPaperFormat          : PaperFormat -> id:string -> outDir:string -> PaperResult   // blocking

/// Resolve publication xrefs to the PMCID the full-text endpoints are keyed on,
/// WITHOUT fetching any full text — the resolve step as a first-class function, so
/// it can be paired with `crawlPaperFormatWithAsync` (one specific format, what R1
/// needs) or `crawlPaperWithAsync` (whichever is available, what R2 needs).
val resolvePmcidWithAsync : CrawlOptions -> PublicationRef list -> Async<string option>
val resolvePmcidAsync / resolvePmcid

/// Sugar for callers holding records rather than refs: publicationRefs >> resolvePmcid.
val discoverPmcidWithAsync : CrawlOptions -> links:seq<Link> -> Async<string option>
```

`crawlPaperWithAsync` (JATS-then-PDF) is unchanged in behaviour and logs; internally it and the two new
surfaces now share private `tryJatsAsync` / `tryPdfAsync` helpers. `discoverAndCrawlWithAsync` is now
`discoverPmcidWithAsync >> crawlPaperWithAsync`.

### `Crawler.fs` (additions)

```fsharp
/// Which R1 AI readiness level format to materialize. The two differ *only* in
/// the paper's file format — the DEE2 archive is byte-identical in both.
type R1Format =
    | R1A   // paper as JATS XML
    | R1B   // paper as PDF

type R1Summary =
    { ProjectDir : string
      Format     : R1Format
      Paper      : PaperResult        // JatsXml (R1A) | Pdf (R1B) | NotFound
      Dee2Path   : string option }    // <outDir>/counts/<StudyAccession>.zip

/// Where each artifact comes from. DUs, not `string option`: under the old
/// signature `None` meant "auto-discover" for the paper but "skip" for DEE2 —
/// opposite senses in the same call — and there was no way to say "no paper".
type PaperSource = PaperFrom of pmcid: string | PaperDiscover | PaperSkip
type Dee2Source  = Dee2From of species: string * studyAccession: string
                 | Dee2Discover of species: string
                 | Dee2Skip

/// Crawl once for MANY formats. Everything except the paper is fetched exactly
/// once and shared: ENA discovery, the BioProject/Study records, the PMCID
/// resolution and the DEE2 download. Only the paper is fetched per format, so
/// `[R1A; R1B]` costs ONE extra request over a single format, not a second crawl.
/// Returns one summary per format, sharing `ProjectDir` and `Dee2Path`.
val crawlR1FormatsWithAsync : CrawlOptions -> R1Format list -> accession:string -> outDir:string
                               -> PaperSource -> Dee2Source -> Async<R1Summary list>
val crawlR1FormatsAsync     : R1Format list -> … -> Async<R1Summary list>
val crawlR1Formats          : R1Format list -> … -> R1Summary list   // blocking

/// Single-format sugar over the above. Calling it twice to get both formats
/// re-runs discovery and re-downloads the archive — use the `Formats` variants.
val crawlR1WithAsync : CrawlOptions -> R1Format -> accession:string -> outDir:string
                        -> PaperSource -> Dee2Source -> Async<R1Summary>
val crawlR1Async     : R1Format -> … -> Async<R1Summary>
val crawlR1          : R1Format -> … -> R1Summary   // blocking
```

Both orchestrators are now **thin compositions** over the resolve + fetch layers, sharing the same
private `paperTarget` / `fetchDee2` steps — `crawlAll` differs only in taking the *full* record crawl
(it writes the XML tree) and letting the paper fall back between formats. Adding a variant no longer
means copying a pipeline.

### Crawl once, materialize twice

R1A and R1B differ *only* in the paper's rendition, so paying for two full crawls to get both would be
pure waste. `crawlR1Formats` fetches the shared parts once and only the paper per format, landing **both**
renditions in one `<outDir>`:

```text
<outDir>/
  counts/<StudyAccession>.zip     # shared
  paper/<PMCID>.jats.xml          # -> R1A
  paper/<PMCID>.pdf               # -> R1B
```

A caller wanting two separate trees copies `counts/` plus its own paper file out of that one directory.
`playground/crawl_r1.fsx` does exactly this: it crawls into a **staging directory outside the git repo**,
then materializes each orphan branch from staging. Staging has to be outside the repo — the branch wipe
between R1A and R1B would otherwise delete the archive that was just downloaded, putting us straight back
to paying for it twice.

A test (`crawlR1Formats fetches everything but the paper exactly once for both formats`) asserts the
request counts directly, so this property cannot regress silently.

No new `CrawlEvent` cases — the existing ones (`Started`, `Discovered`, `Fetching`, `Parsed`,
`DiscoveredPaperRef`, `FetchedPaperFormat`, `FetchPaperFailed`, `FetchedBundle`, `BundleNotFound`,
`Completed`) already cover every step, so `Log.fs` is untouched. No fsproj change either: `Paper.fs` and
`Dee2.fs` already compile before `Crawler.fs`.

## Implementation notes

### Lean fetch — the one place R1 is not a copy of `crawlAll`

R1 writes no INSDC XML, and reads records for exactly two things: the **publication xrefs** (which drive
paper auto-discovery) and the **Study accession** (the DEE2 lookup key). Both live on the BioProject and
the Study.

So `crawlR1WithAsync` deliberately does **not** call `Fetch.crawlCore` — that would also fetch every
BioSample, Experiment and Run just for them to be discarded. It calls the existing private helpers
directly:

```fsharp
let! discovered  = Fetch.discoverAsync options accession
let! bioProjects = Fetch.entity options "BioProject" BioProject.readString discovered.BioProjects
let! studies     = Fetch.entity options "Study"      Study.readString      discovered.Studies
```

On a large project this is the difference between a handful of requests and thousands. A regression test
(`R1 writes no INSDC XML and never fetches sample, experiment or run records`) locks it.

### Paper

An id is resolved first — caller-supplied (`paperId = Some pmcid`) or auto-discovered from the records'
own PUBMED/PMC xrefs (`paperId = None` → `Paper.discoverPmcidWithAsync`) — then fetched in exactly this
readiness format's paper format (`R1A → PaperFormat.Jats`, `R1B → PaperFormat.Pdf`).

### DEE2

Identical to `crawlAll`'s DEE2 step: keyed on the first discovered Study's **accession** (SRP/ERP/DRP), the
archive-assigned `Accession` — **not** the submitter `Alias`, which for a GEO-origin study is a GEO series
id DEE2 never keys on. `dee2Species = None`, or no study discovered → step skipped.

### Independent failure

The paper and the archive fail independently: a paper that has no full text in the requested format does
not stop the archive landing, and vice versa. `R1Summary` reports each, and `playground/crawl_r1.fsx` only
commits a branch when **both** landed.

## Testing

Offline, in the existing stubbed-`Fetch`/`FetchBytes` idiom (`R1CrawlerTests` in `tests/.../Tests.fs`).
**No new fixtures** — every file needed was already committed for R2.

> **Stub-ordering gotcha** (inherited from the R2 tests): the DEE2 search URL carries the study accession
> too (`…&accessionsearch=DRP003416`), so a URL-substring stub must match `"search2.sh"` **before** the
> accession-keyed INSDC branches.

> **The stub records the URLs it was asked for, and the lean-fetch test asserts on them.** A `failwith`
> stub alone would *not* catch an accidental sample/experiment/run fetch: `Fetch.entity` swallows a failed
> batch (it logs `Failed` and yields no records), so the failure would be silently absorbed and the test
> would pass regardless. Asserting on what was *requested* is the only honest check.

Cases: R1A lands JATS + archive and no PDF; R1B lands PDF + archive and no JATS; no INSDC XML is written
and no sample/experiment/run record is fetched; an R1A whose JATS is unavailable still lands the archive
(and does **not** take the available PDF); `dee2Species = None` skips DEE2; `paperId = None` routes through
discovery. Plus, on the paper module: forced-JATS never touches the binary seam, forced-PDF never probes
the XML seam, and forced-JATS returns `NotFound` rather than falling back.

## Verification

- `./build.sh` — builds clean, zero missing-XML-doc warnings.
- `./build.sh runtests` — the new tests pass **and every existing crawler test still passes** (the
  `crawlPaperWithAsync` refactor is behaviour-preserving; that is the main regression risk here).
- `dotnet fsi playground/crawl_r1.fsx` — crawls a real project into orphan branches `R1A` and `R1B`,
  committing + pushing each only when it came out complete. The two trees should differ **only** in the
  paper file.

## Known consequences

- **"Source obscured" is weaker than the R1 definition claims.** Shipping the archive verbatim leaves the
  study accession in the zip filename and run accessions in the DEE2 count headers. That is inherent to
  "verbatim" — and it is exactly what made R1C redundant. Truly hiding the INSDC origin would mean renaming
  the zip and re-headering the counts, which would reintroduce the IR dependency this definition removes.
- **The DEE2 archive is committed through Git LFS.** It is a multi-MB binary; as an ordinary blob it would
  sit in every clone of the curation repo forever, and rewriting history to undo that after a push is
  miserable. Both playground scripts therefore run `git lfs install --local` and write a `.gitattributes`
  routing `counts/**` through LFS. Two ordering traps, **both of which fail silently** (the commit succeeds
  and the zip is simply a raw blob): the `filter.lfs.*` config must exist *before* `git add`, and
  `.gitattributes` must be on disk *before* `git add` — which means it has to be re-written after every
  branch wipe, since the wipe deletes it. The scripts re-establish it at the end of the branch-init step and
  print `git lfs ls-files` after each commit, because that is the only place the mistake becomes visible.
  `git-lfs` is provisioned by the `ghcr.io/devcontainers/features/git-lfs:1` devcontainer feature.

## Future extensions (noted, not implemented)

- **Whole-project DEE2** — a project with multiple studies currently keys on the first one only.
- **PDF text extraction** — turning an R1B PDF into JATS-equivalent metadata so the ingest pipeline can
  consume both formats.
