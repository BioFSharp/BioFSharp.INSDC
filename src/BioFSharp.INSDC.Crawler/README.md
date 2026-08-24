# BioFSharp.INSDC.Crawler

Collects [INSDC](https://www.insdc.org/) records and related raw artifacts from
public archives.

Given a project or study accession, the crawler discovers connected studies,
samples, experiments, and runs through ENA, fetches their XML records, and can
persist both records and connectivity through
[`BioFSharp.INSDC.SQLite`](https://www.nuget.org/packages/BioFSharp.INSDC.SQLite).

It can also materialize a round-tripped INSDC XML tree, Europe PMC JATS or PMC
Open Access PDF full text, and DEE2 count bundles. `crawlAll` composes the raw R2
layout. `crawlR1Formats` composes R1A and R1B while sharing discovery and
non-paper downloads. R1 and R2 are crawler formats, not ArcIR F2
implementations.

Crawls are strict by default: an exhausted record batch fails after in-flight
work finishes instead of silently returning a partial result. Explicit
inspection workflows can opt into `CrawlOptions.ContinueOnPartialFailure`.
Cancellation is never retried; transient failures use bounded retry delays.
`Fetch` and `FetchBytes` remain injectable, so the normal suite uses committed
offline fixtures.

Artifacts are written through same-directory temporary files and atomically
renamed. Resume skips valid existing XML, JATS, PDF, and ZIP artifacts;
malformed binary responses never replace the final path. SQLite persistence
explicitly opts into the store's documented soft-reference foreign-key mode.

Because its HTTP stack requires .NET 6 or later, this package targets `net8.0`
instead of the `netstandard2.0` baseline of the other shipped libraries. It is
nevertheless a normal packed and published NuGet package.

Part of [BioFSharp.INSDC](https://github.com/BioFSharp/BioFSharp.INSDC).
Released under the MIT license.
