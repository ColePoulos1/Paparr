# Performance harnesses

## Offline ingestion pipeline

`tools/Paparr.IngestionBenchmarks` starts a disposable PostgreSQL container and a test host, creates distinct synthetic PDF/EPUB files, discovers stable files, extracts metadata, explicitly approves each choice through the ingestion service, publishes verified copies, and runs an integrity scan. It verifies every published hash and retained original. The background timer and external providers are disabled; the driver advances the stability clock by five seconds instead of sleeping. Container startup, migrations, and fixture creation are measured separately.

```powershell
dotnet run --configuration Release --project tools/Paparr.IngestionBenchmarks -- --files 10 --output .tools/ingestion-run.json
```

`--files` accepts 2–100, default 10. `--content-mib` accepts 0–4 per file; zero keeps the tiny fixtures. Larger fixtures contain real PDF text pages and referenced EPUB XHTML chapters stored without compression. Requested content and actual input files are bounded to 512 MiB; originals and library copies occupy roughly twice the input space. Output files are created without replacement. The harness uses only its own temporary roots and database and removes them when finished.

```powershell
dotnet run --configuration Release --project tools/Paparr.IngestionBenchmarks -- --files 10 --content-mib 4 --output .tools/ingestion-larger.json
```

The current harness separately measures staging journal settlement, then hashes both original and published files. Successful publication has already renamed each temporary file, so settling these rows does not measure orphan deletion or reclaimed bytes. Fixture generation, final verification, and status probes are excluded from processing timings.

One Windows x64 run on October 21, 2026, with .NET 10.0.9 and eight logical processors, processed ten tiny fixtures totaling 8,777 bytes:

| Phase | Elapsed |
| --- | ---: |
| Initial discovery observation | 20 ms |
| Stable-file discovery and persistence | 661 ms |
| Metadata extraction and review queue | 807 ms |
| Explicit approval and publication | 322 ms |
| Integrity scan | 329 ms |
| PostgreSQL container startup | 8,845 ms |
| Host startup and migrations | 2,125 ms |

All ten imports completed, all original files remained present, all published bytes matched their recorded hashes, and all ten audit results were verified. Raw local evidence is `.tools/ingestion-20261021.json`. These are observations from a small offline workload, not a sustained throughput or large-book benchmark. The harness calls the real services directly; it does not measure review time, HTTP overhead, provider latency, background scheduling, or network storage. Docker and filesystem cache state affect timings.

The same harness with publication journaling enabled completed 100 fixtures totaling 87,776 bytes: discovery took 1,754 ms, metadata extraction 2,762 ms, explicit approval and publication 4,390 ms, and the integrity scan 740 ms. It retained every original file, verified all 100 published hashes, and processed all 100 audit results without faults. Raw evidence is `.tools/ingestion-undo-100.json`. This checks a larger queue of tiny files; it does not establish large-book throughput or directly rehash retained originals after completion.

On October 22, 2026, the extended harness passed two files with 1 MiB of requested content each and ten files with 4 MiB each. The latter produced 42,917,249 actual input bytes: stable discovery took 2,492 ms, metadata extraction 4,231 ms, approval/publication 5,174 ms, integrity scanning 1,989 ms, and settlement of ten journal rows 1,145 ms. Final source and destination hashes matched, all ten audit results were verified, and no partial files remained. Evidence is `.tools/ingestion-release-2x1.json` and `.tools/ingestion-release-10x4.json`.

These generated, uncompressed text workloads do not model image-heavy books, rendering, storage contention, or sustained production traffic. The measurements ran alongside other local verification and are individual observations, not comparable throughput claims.

## Discovery hashing microbenchmark

`tools/Paparr.Benchmarks` compares repeated discovery hashing with and without the stable-file hash cache. It creates synthetic files in a unique temporary directory, performs an isolated hash warmup, measures uncached hashing first and cached hashing second, and emits JSON with elapsed time, actual hash calls, bytes hashed, runtime details, and matching aggregate hashes. It deletes only the temporary directory it created. An explicitly requested `--output` file is created without overwriting an existing file.

Run it from the repository root in Release configuration:

```powershell
dotnet run --configuration Release --project tools/Paparr.Benchmarks -- --files 32 --size-mib 1 --passes 6
dotnet run --configuration Release --project tools/Paparr.Benchmarks -- --files 64 --size-mib 4 --passes 8 --output .\hash-results.json
```

Options are bounded: `--files` 1–256, `--size-mib` 1–16, and `--passes` 2–30. Total synthetic file content cannot exceed 512 MiB. Defaults are 32 files, 1 MiB each, and 6 passes.

This is a local microbenchmark of `FileHashService` and `StableFileHashCache`, not a production throughput claim. It does not model database queries, directory traversal, simultaneous workloads, or storage contention. Operating-system filesystem caches, runtime warmup, and device state affect timings; compare repeated runs on the same machine and use hash-call/byte counts as the more stable signal. The cache's five-minute revalidation interval is not exercised during this short harness run.

## Recorded run: October 21, 2026

Release build, .NET 10.0.9, Windows x64, eight logical processors. Default workload: 32 files of 1 MiB, six passes, cold application cache on the first cached pass.

| Measurement | Uncached | Cached |
| --- | ---: | ---: |
| Hash calls | 192 | 32 |
| Bytes hashed | 192 MiB | 32 MiB |
| Elapsed | 2,066 ms | 427 ms |

Both runs produced the same aggregate hash. The stable result is 83% fewer bytes read for this unchanged-file workload. The elapsed times are one local observation; the uncached run executes first and both runs benefit from operating-system caching. These results do not measure complete ingestion or network storage performance.
