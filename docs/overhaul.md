# Overhaul

## Milestones

| Area | Implemented | Remaining |
| --- | --- | --- |
| Foundation | AGENTS.md, five project skills, SDK/runtime settings, verification script, CI, dependency updates, measured source coverage | Published CI history and coverage trends |
| Safe imports | Durable queue, revision guards, publication fences, verified publication, collision protection, explicit retry, durable source cleanup, staging maintenance, retained-source undo | Larger recovery workloads |
| Metadata | Real PDF/EPUB parsing, provenance, matching evidence, provider isolation, bounded cache, cooldowns, checked ISBNs and edition details | Calibration against labeled real-world decisions |
| Review experience | Generated API types with drift checks, search, paging, manual edition choices, destination previews, timeline, draft preservation | More comparative review tools as library size grows |
| Product features | Edition details in review, read-only resumable integrity scans, library health, duplicate-file history, verified downloads, retained-source undo, captured folder profiles, synthetic offline demo | Separate edition/file model |
| Library integration | Portable standalone library structure and explicit verified Calibre handoff | Separate edition/file model and synchronization design |
| Operations | Same-origin proxy, non-root containers, persistent storage, owner sign-in, CSRF/host/origin checks, worker and staging diagnostics, tested restoration, proxy trust regression and local TLS checks | Public TLS provisioning |
| Release | Screenshots, architecture notes, repeatable deployment checks, reproducible source packaging, hashing and offline ingestion harnesses | Published CI history, full upgrade matrix, sustained/larger-book throughput measurements |

## Acceptance criteria exercised

- Approval is idempotent; concurrent requests cannot create two accepted books.
- A failed completion commit retains the source and leaves replayable publication state.
- An existing destination with different bytes is preserved.
- Source cleanup survives interruption, retains changed originals, and never fails a completed import.
- Stale publishers cannot alter a recovered import. Maintenance cleans only confirmed journaled temporary files, preserves current attempts, and retains unconfirmed or untracked files.
- Undo preserves the recorded original, returns the import to review, and survives cancellation or a failed completion commit. Old operation IDs cannot undo reapproved books; cleanup and integrity scans coordinate through PostgreSQL locks.
- PostgreSQL tests run real migrations, constraints, and concurrent requests.
- Provider errors preserve local review, and rate-limited providers do not block each other.
- Refreshes preserve unsaved metadata drafts and ignore stale responses.
- Integrity scans identify changed/missing files, remain read-only, and resume from a durable cursor.
- A disposable Docker deployment imports fixtures, survives recreation, and restores its database and library into new locations.

See [verification](verification.md) for executed checks and their limits.

## Baseline

The original builds passed, but there were no automated tests. PDF extraction used ZIP parsing; retry could strand pending jobs; publication overwrote destinations and deleted sources before committing records. Dependency audits reported vulnerabilities. The Compose database volume and frontend API origin needed correction.

## Next implementation priorities

1. Separate edition/file modeling; define identities and migration before adding synchronization.
2. Sustained and concurrent workload measurements, published CI history, and a broader upgrade matrix.

The release stopping point and next tasks are recorded in [the handoff](handoff.md).
