# Overhaul

## Milestones

| Area | Implemented | Remaining |
| --- | --- | --- |
| Foundation | AGENTS.md, five project skills, SDK/runtime settings, verification script, CI, dependency updates, measured source coverage | Published CI history and coverage trends |
| Safe imports | Durable queue, revision guards, leases, verified publication, collision protection, explicit retry, durable source cleanup | Staging-file maintenance |
| Metadata | Real PDF/EPUB parsing, provenance, matching evidence, provider isolation, bounded cache, cooldowns | Edition identifiers, calibrated automatic decisions |
| Review experience | TypeScript workspace, search, paging, manual edits, destination previews, timeline, draft preservation | Generated API types and richer metadata comparison |
| Product features | Read-only resumable integrity scans, library health screen, duplicate-file history, verified downloads, synthetic offline demo | Edition comparison, safe undo, folder profiles |
| Library integration | Portable standalone library structure | Explicit Calibre handoff and edition/file model |
| Operations | Same-origin proxy, non-root containers, persistent storage, owner sign-in, CSRF/host/origin checks, worker stage diagnostics, tested restoration | Remote TLS deployment checks and staging maintenance |
| Release | Screenshots, architecture notes, changelog, repeatable deployment checks, discovery hashing benchmark | Release packaging, full upgrade matrix and ingestion throughput measurements |

## Acceptance criteria exercised

- Approval is idempotent; concurrent requests cannot create two accepted books.
- A failed completion commit retains the source and leaves replayable publication state.
- An existing destination with different bytes is preserved.
- Source cleanup survives interruption, retains changed originals, and never fails a completed import.
- PostgreSQL tests run real migrations, constraints, and concurrent requests.
- Provider errors preserve local review, and rate-limited providers do not block each other.
- Refreshes preserve unsaved metadata drafts and ignore stale responses.
- Integrity scans identify changed/missing files, remain read-only, and resume from a durable cursor.
- A disposable Docker deployment imports fixtures, survives recreation, and restores its database and library into new locations.

See [verification](verification.md) for executed checks and their limits.

## Baseline

The original builds passed, but there were no automated tests. PDF extraction used ZIP parsing; retry could strand pending jobs; publication overwrote destinations and deleted sources before committing records. Dependency audits reported vulnerabilities. The Compose database volume and frontend API origin needed correction.

## Next implementation priorities

1. Edition identifiers and comparison, and an explicit Calibre export workflow.
2. Retained-source undo with previews and filesystem/database recovery tests.
3. Staging maintenance and full ingestion throughput measurements.
