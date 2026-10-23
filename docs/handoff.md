# Paparr 0.2.0 handoff

Release checkpoint: October 22, 2026, branch `codex/overhaul`. No tag or release was published at that checkpoint. The subsequent hosted verification attempt is recorded below.

## Hosted verification follow-up

Continuation branch: `codex/hosted-release-verification`, based on `a79eaaafb4a7a4a2abb45e7ba7365607e8e3edc0`. The existing [Verify run on main](https://github.com/ColePoulos1/Paparr/actions/runs/39877408260) tested that commit: backend, Windows unit, and frontend jobs passed; proxy and deployment jobs failed. The failing steps are **Reject spoofed proxy headers and verify local TLS** and **Install, import, recreate, and restore**. Public annotations only report exit code 1; authenticated logs and uploaded artifacts still need inspection before attributing either failure.

Local proxy and TLS checks, and the packaged deployment with browser and Calibre checks, passed again on Windows. See [verification](verification.md#october-22-2026-hosted-verification-follow-up) for evidence. These local passes do not resolve the hosted failures. The next action is to inspect both failed job logs, reproduce and fix their causes, push this continuation branch, and verify all hosted jobs and artifacts before publishing a release.

## Current scope

The overhaul now includes durable discovery and approval, verified publication, publication fences and staging maintenance, recoverable source cleanup, retained-source undo, metadata provenance and edition fields, captured folder profiles, duplicate history, verified downloads and Calibre exports, resumable integrity scans, owner authentication, and worker diagnostics. The UI is responsive and uses generated API contracts with drift checks.

Folder profiles are startup configuration, not editable application records. Each import captures its selected profile; later configuration changes affect new jobs. Undo keeps that snapshot. All imports require review. The library has one book record per accepted import, so it does not yet model several files or formats belonging to one edition.

Start with [AGENTS.md](../AGENTS.md), [architecture](architecture.md), [deployment](../DEPLOYMENT_GUIDE.md), [verification](verification.md), and the five workflows under `.agents/skills`. Keep comments brief and explain constraints rather than narrating code.

## Verification and release use

```powershell
git status --short
dotnet tool restore
pwsh scripts/verify.ps1 -Coverage
pwsh scripts/Test-Deployment.ps1 -Browser -Calibre
```

Use Node 24 and the pinned .NET SDK. Docker is required for PostgreSQL and deployment checks; Calibre is only required with `-Calibre`. The full suite contains 147 backend unit, 97 PostgreSQL integration, 56 React component, and 12 Chromium desktop/mobile cases. Backend source line coverage is 93.7%, excluding generated migrations. See the verification record for execution evidence and limitations.

The packaged checks create synthetic books, a disposable Compose project, and their own database and filesystem roots. They exercise profile routing, backup/restore, signed-in browser review, undo and reapproval, verified downloads, and real isolated Calibre imports. They clean up their own Docker resources. Never redirect them to personal libraries.

Create an archive from a clean committed tree with:

```powershell
pwsh scripts/New-ReleaseBundle.ps1 -Destination .tools/release-0.2.0
```

The destination must be new. The manifest records the source commit and archive SHA-256. The archive contains source, not binaries or personal configuration. It can be used with the documented Docker build. API and frontend package versions are `0.2.0`; the changelog records the release checkpoint. Public distribution and hosted CI remain separate work.

Local evidence is ignored by Git:

- `.tools/verify-release.log` and `.tools/coverage-2a9cfa3f818041a083ba656e1b504d17/summary.md`.
- `.tools/deployment-release.log` and the release deployment's `results.json` and browser screenshots.
- `.tools/ingestion-release-2x1.json`, `.tools/ingestion-release-10x4.json`, and `.tools/ingestion-release-100.json`.

The older persistent synthetic demo is `paparr-overhaul-smoke` at `http://127.0.0.1:5187`. Its `.tools/smoke.env`, password file, mounts, and database are private local artifacts. It predates this release and may need rebuilding. Do not print its credentials or treat it as release evidence. Other smoke projects are disposable and use unique names.

## Next work

| Priority | Work | First step and acceptance criteria |
| --- | --- | --- |
| 1 | Hosted release verification | Resolve the proxy and deployment failures in run `39877408260`, push the continuation branch, and inspect all hosted results. Publish the source archive or container images only after those checks pass. |
| 2 | Edition and file identities | Write an ADR before changing tables. Decide how a work, edition, file, and import relate; handle EPUB/PDF variants, conflicting ISBN evidence, legacy books, and identical-content observations. Migrate existing rows without resetting data and preserve download, audit, undo, and provenance behavior. |
| 3 | Profile management and explicit rerouting | Add owner-only validation and previews if profiles move into the UI/database. Keep historical snapshots immutable. Rerouting an existing book must be a separately journaled operation with collision, crash, and stale-request tests. |
| 4 | Retained-file operations | Add an owner-only inspection view for retained stages and failed undo operations, using safe relative paths and actionable history. Offer only operations with proven ownership. Never infer ownership from `.partial` names or age, and never sweep legacy unknown files automatically. |
| 5 | Operational scale | Measure mixed compressed/image-heavy books, simultaneous API/worker activity, process interruption, and larger histories. Use PostgreSQL query plans before adding indexes or journal retention. Track database growth and settled-history pagination. |
| 6 | Metadata quality | Build a small labeled, redistributable evaluation set for provider matching and edition ambiguity. Keep HTTP fixtures controlled. Scores remain similarities, not probabilities; a valid ISBN checksum does not identify the file's edition. |
| 7 | Compatibility and upgrades | Expand the original-schema and intermediate-schema upgrade checks into a matrix. Add Firefox/WebKit journeys, actual IPv6 proxy requests, and any intended network-filesystem checks. Define support for case-insensitive Unix volumes before changing path matching. |

Public TLS provisioning, multi-user authorization, external identity providers, direct Calibre synchronization, and automatic approval are not implemented. They need their own design and threat model rather than extending owner-session assumptions implicitly.

## Relevant implementation boundaries

- `ImportScanner` persists the first content-hash owner and its profile snapshot; subsequent copies become `DuplicateSource` observations.
- `EbookIngestionService`, `StagingService`, and `LibraryPublisher` separate durable claims, exclusive file creation, hash verification, and fenced promotion. Parent PostgreSQL row locks coordinate competing publication and maintenance.
- `ImportUndoService` binds a request ID to the original book and paths. A completed old request must never undo a later reapproval. Missing-destination reconciliation is allowed only after durable undo admission and fresh verification of the retained original.
- `SourceCleanupService` claims durably, then locks and rechecks ownership before deleting. Preserve that final check when adding new transitions.
- `LibraryOperationAdmission` serializes scan/undo admission. Their current exclusion protects audit snapshot counts and cursors; do not remove it without replacing those guarantees.
- `LibraryPaths.ImportDestination` is shared by preview and approval. Saved collection segments remain untrusted and are revalidated during publication.
- `LibraryDownloadService` hashes and serves the same open handle. Exports reuse those verified bytes and do not alter stored ebooks.
- `PaparrFactory` owns its Npgsql data source, shared by its child hosts and independent test contexts. Preserve explicit disposal; implicit connection-string pools previously exhausted PostgreSQL during the full suite.

## Practical limits

The release evidence covers temporary local files and Docker bind mounts on Windows/Linux, not power loss, hostile filesystem replacement, or network storage. Mobile journeys use Chromium viewport emulation, not Safari. Local TLS uses a self-signed test certificate; public CA provisioning is unverified. Folder matching follows Windows case-insensitive and Linux ordinal case-sensitive rules. Benchmark results describe generated offline workloads and do not establish sustained production throughput.

Keep incomplete future work out of the release checkpoint. Start the next feature from a clean tree, follow the relevant project skill, reproduce defects before fixing them, and update this document when the roadmap changes.
