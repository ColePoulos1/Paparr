# Verification record

## October 21, 2026

Executed locally on Windows with .NET SDK 10.0.301/runtime 10.0.9, Node 24, Docker Desktop, and PostgreSQL 18.6 containers. Fixtures and databases were disposable; no personal library was used.

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings and errors |
| Backend unit tests | 77 passed |
| PostgreSQL integration tests | 47 passed |
| React component tests | 39 passed |
| Desktop/mobile browser tests with controlled API responses | 10 passed |
| Frontend lint and production build | Passed |
| NuGet transitive dependency audit | No known vulnerabilities reported |
| Clean Docker frontend dependency installation | No known vulnerabilities reported |

The complete `pwsh scripts/verify.ps1` passed 165 tests, lint, and the production builds. Eight additional PostgreSQL regressions then passed with the full integration suite, bringing the executed total to 173 tests. Production code did not change during those test additions.

Integration tests exercise real PostgreSQL migrations and constraints, concurrent approvals, publication commit failures and recovery, source cleanup interruption, integrity scan recovery, revocable owner sessions, CSRF validation, password rotation, and trusted proxy handling. An upgrade test seeds the original schema before applying current migrations and checks that its import and book survive.

`pwsh scripts/Test-Deployment.ps1 -Browser` passed all nine packaged checks in a fresh Compose project: installation and extraction, duplicate discovery and retention, verified publication with source retention, container recreation, read-only integrity checks, database/library restoration into separate locations, owner-only verified downloads, session persistence and revocation, and desktop/mobile browser review. The browser run displayed duplicate history, edited missing metadata, confirmed approval committed, scanned both library files, downloaded the original EPUB with a matching hash and filename, inspected worker stages, displayed a failed import, and confirmed a copied session stopped working after sign-out. No browser page errors or page-wide horizontal overflow were observed. The script removed its own containers, volumes, and images afterward.

Local evidence is saved under `.tools/paparr-smoke-ba43cc78ba2f/results.json` and its `browser` directory; these disposable artifacts are intentionally ignored by Git.

The retained synthetic demo at `http://127.0.0.1:5187` was rebuilt with the current images. Its existing library survived the update, and a separate browser check passed owner sign-in, a matching-hash download, desktop/mobile worker status, and sign-out. This demo uses only generated books and its own database and mounts.

The [discovery hashing benchmark](performance.md) measured 83% fewer bytes hashed across six passes over unchanged files. Its local timing is a microbenchmark, not an ingestion throughput claim.

## Duplicate history and filesystem regressions

Duplicate tests force concurrent first discovery of identical content at different paths, check repeated discovery and unchanged job revisions, and exercise historical pagination and path privacy. Filesystem regressions first reproduced incorrect cleanup completion and missing-file classification when a recorded file was replaced by a directory, then passed after the fix.

Worker diagnostics tests exercise progress, failure recovery, cancellation, monotonic duration and delay detection, wall-clock changes, owner authorization, and path privacy. A real cycle records all four stages; a failed discovery leaves earlier stage results available and returns a sanitized explanation.

Download tests verify EPUB/PDF attachment names and types, Unicode filenames, authorized reads, partial and unsatisfiable ranges, and conflicts for unsafe, missing, changed, or oversized files. Failed requests leave file bytes and import status intact. UI tests cover in-page errors, repeated clicks, and cancellation when leaving the view.

## Source coverage

Combined backend coverage is 1,432 of 1,538 executable source lines (93.1%), excluding generated migrations and `.g.cs` files. The unit and integration reports describe the same API source revision. This is line coverage, not combined branch coverage; subprocesses such as the packaged password helper are outside the managed test collector.

Coverage analysis led to regressions for changed sources, exhausted retries, cleanup disabled after restart, concurrent cleanup claims, and session renewal after logout. `scripts/Measure-Coverage.ps1` unions report hits by file and line. Its fixture check verifies complementary hits, duplicate classes, generated-file exclusions, path separator normalization, and failure when reports are absent. CI is configured to publish that summary; a hosted CI run is still pending.

## Scope and limits

- Browser tests use Chromium, including mobile viewport emulation; they do not establish compatibility with Safari or Firefox.
- Provider tests use controlled responses. Live metadata services and remote HTTPS termination were not exercised.
- Storage checks cover temporary local files and Docker bind mounts. Network filesystems, power loss, and hostile concurrent filesystem changes are outside this evidence.
- Dependency audits are point-in-time results. GitHub Actions is configured, but no hosted CI run or published release is claimed here.
- The original-schema upgrade test is one supported migration path, not a complete upgrade matrix.
