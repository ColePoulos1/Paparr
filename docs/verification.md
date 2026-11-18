# Verification record

## October 23, 2026 hosted verification completed

All five jobs passed in [GitHub Actions run 39880822743](https://github.com/ColePoulos1/Paparr/actions/runs/39880822743) for commit `d6d47c4d5dda3061e14b2a72d2d93dd69cf227ad`: Windows unit, backend, frontend, proxy, and deployment. Downloaded artifacts were inspected on October 23. The backend reports contain 147 passing unit and 97 passing PostgreSQL integration cases, with 2,249/2,401 executable lines covered (93.7%). Windows independently passed the same 147 unit cases. Frontend logs confirm 56 component tests, 12 desktop/mobile Chromium cases, lint, production build, and generated-contract checks.

The deployment artifact records twelve successful checks, including restoration into separate storage, approval into the restored collection, verified downloads and export archives, session revocation, and browser undo/reapproval. The TLS artifact records nine successful checks. Desktop library and mobile undo screenshots were inspected. Hosted deployment does not install Calibre; the fourteen-check local run below separately covers two real Calibre imports. Artifacts and logs are saved under `.tools/hosted-fixed-artifacts` and `.tools/hosted-fixed.log`.

The initial hosted failures were in disposable test setup:

- Docker rejected static proxy addresses on an automatically configured subnet. The TLS fixture now obtains an available subnet from Docker and recreates its owned network with that explicit subnet. The corrected local run also passed all nine checks (`.tools/paparr-tls-82dbe38e1be6/results.json`).
- Restoring with `Copy-Item` gave the runner ownership of nested collection directories. Making only the root writable left the API unable to publish a second book inside `Fiction`. A disposable Linux container reproduced the failure and verified nested publication and deletion after restoring API ownership (`.tools/restore-permissions-regression.log`). The deployment fixture now restores ownership recursively on its own copied library before starting the API.

Browser failure artifacts now preserve stack traces with the test password redacted. No application behavior, personal library, or production database was changed by these fixture fixes.

## October 22, 2026 hosted verification follow-up

The initial [GitHub Actions run 39877408260](https://github.com/ColePoulos1/Paparr/actions/runs/39877408260) checked commit `a79eaaafb4a7a4a2abb45e7ba7365607e8e3edc0` on `main`. The backend, Windows unit, and frontend jobs passed. The proxy and deployment jobs failed. Their logs and artifact contents subsequently established the fixture defects described above.

Local checks on the same source commit used temporary files and disposable PostgreSQL/Compose resources:

- `dotnet tool restore` succeeded; the release build had zero warnings and errors, all 147 backend unit and 97 PostgreSQL integration tests passed, and backend line coverage remained 2,249/2,401 (93.7%).
- `scripts/verify.ps1 -Coverage` completed successfully: 56 React component tests and 12 desktop/mobile Chromium cases also passed, along with frontend lint, production build, generated API contract checks, and coverage/release-bundle fixtures (312 tests total).
- `scripts/Test-ProxyTrust.ps1` passed all six spoofing, trusted-peer, and invalid-configuration cases. `scripts/Test-TlsDeployment.ps1` passed all nine checks; evidence is `.tools/paparr-tls-515066664b20/results.json`.
- `scripts/Test-Deployment.ps1 -Browser -Calibre` passed all fourteen checks, including backup/restore, browser undo/reapproval, and two real isolated Calibre imports. Evidence is `.tools/paparr-smoke-8bc5d5e7df2b/results.json` and its `browser` directory. Both deployment scripts cleaned up their Docker resources.
- The transitive NuGet audit and clean Docker frontend dependency installation reported no known vulnerabilities.
- Source packaging succeeded into `.tools/release-hosted-a79eaaa`, with its manifest identifying the tested commit and archive SHA-256. This archive has not been published.

Logs are `.tools/verify-hosted-preflight.log`, `.tools/deployment-hosted-preflight.log`, `.tools/proxy-hosted-preflight.log`, `.tools/tls-hosted-preflight.log`, and `.tools/dependency-hosted-preflight.log`. These local Windows passes preceded the fixes and successful Linux hosted run above.

## October 22, 2026 release checkpoint

Executed locally on Windows with .NET SDK 10.0.301/runtime 10.0.9, Node 24, Docker Desktop, and PostgreSQL 18.6 containers. Fixtures and databases were disposable; no personal library was used.

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings and errors |
| Backend unit tests | 147 passed |
| PostgreSQL integration tests | 97 passed |
| React component tests | 56 passed |
| Desktop/mobile browser tests with controlled API responses | 12 passed |
| Frontend lint and production build | Passed |
| NuGet transitive dependency audit | No known vulnerabilities reported |
| Clean Docker frontend dependency installation | No known vulnerabilities reported |

The current suites passed 312 tests in total through `scripts/verify.ps1 -Coverage`, with lint, production builds, generated-contract checks, and the coverage/release script fixtures. API schema tests cover response nullability, request constructor validation, enum values, and path privacy. Deliberately changing a generated contract caused the drift check to fail; restoring it passed. Fresh npm and transitive NuGet advisory checks reported no known vulnerabilities.

Integration tests exercise real PostgreSQL migrations and constraints, concurrent approvals, publication commit failures and recovery, source cleanup interruption, integrity scan recovery, revocable owner sessions, CSRF validation, password rotation, and trusted proxy handling. An upgrade test seeds the original schema before applying current migrations and checks that its import and book survive.

`pwsh scripts/Test-Deployment.ps1 -Browser -Calibre` passed all fourteen packaged checks in a fresh Compose project: installation and extraction, duplicate discovery and retention, verified publication with source retention, container recreation, read-only integrity checks, database/library restoration into separate locations, owner-only verified downloads and Calibre archives, session persistence and revocation, desktop/mobile browser review, captured folder routing, undo and reapproval, and actual Calibre imports into isolated libraries. The browser run edited missing metadata including ISBN, publisher, year, and language, confirmed approval committed, scanned both library files, downloaded matching original bytes, exported approved metadata, inspected worker stages, undid and reapproved an import, displayed a failed import, and confirmed a copied session stopped working after sign-out. A filesystem check confirmed the original remained unchanged after undo and reapproval; the republished download matched its hash. Calibre 5.44 preserved the approved title, author, ISBN, publisher, year, and language from the browser export. No browser page errors or page-wide horizontal overflow were observed. The script removed its own containers, volumes, and images afterward.

The latest packaged run includes staging diagnostics on desktop and mobile and a mobile undo preview. Evidence is saved under `.tools/paparr-smoke-9cbaade76c4e/results.json` and its `browser` directory; these disposable artifacts are intentionally ignored by Git. Screenshots were inspected and updated. Earlier visual inspection prompted radio-alignment and action-spacing fixes, followed by frontend verification and a rebuilt demo check. The packaged browser driver also has a syntax check before deployment starts.

The retained synthetic demo at `http://127.0.0.1:5187` was rebuilt before the staging checkpoint. Its existing library survived that update, and a separate browser check passed owner sign-in, a matching-hash download, desktop/mobile worker status, and sign-out. This demo uses only generated books and its own database and mounts.

The [performance harnesses](performance.md) measured 83% fewer bytes hashed across six passes over unchanged files and exercised the complete offline import pipeline on batches of ten and 100 tiny PDF/EPUB fixtures. These are bounded local observations, not sustained throughput claims.

## Proxy trust and local TLS

`scripts/Test-ProxyTrust.ps1` reproduced the former spoofed-header behavior, then passed untrusted-header rejection, trusted HTTPS and HTTP forwarding, and invalid CIDR/injection rejection. The real UI image also started with its production entrypoint and Nginx command. The header-forwarding test uses an IPv4 peer with IPv4/IPv6 ranges configured.

`scripts/Test-TlsDeployment.ps1` passed nine checks with a local self-signed certificate, distinct proxy addresses, and a disposable database/network. It checked anonymous access, CSRF, Secure/HttpOnly/SameSite cookies, sign-in, direct HTTP spoof rejection, proxy replacement of client scheme headers, cross-origin rejection, authenticated access, sign-out revocation, and readiness. Evidence is `.tools/paparr-tls-ccb68d4cab84/results.json`. The test removed its resources. Public CA validation and remote certificate provisioning remain outside this check.

## Edition and export coverage

Real EPUB fixtures cover ISBN URNs and embedded edition fields. Controlled provider responses cover explicit field requests, multiple editions, malformed identifiers, and work-level provenance. PostgreSQL tests verify selected metadata persistence, manual input bounds and non-inheritance, and preservation of existing rows through the edition migration. Export tests exercise EPUB/PDF ZIP contents, hash preservation, Unicode/XML text, missing and changed files, owner access, oversized metadata, and unchanged source/library/database state.

Null-character regressions reproduced manual requests returning HTTP 500 and providers admitting text PostgreSQL cannot store. Manual title, author, and publisher inputs now return HTTP 400 before changing a job; subsequent valid edits still succeed. Unusable provider choices are skipped independently, invalid optional publishers are omitted, and PDF metadata can fall back to the filename.

## Duplicate history and filesystem regressions

Duplicate tests force concurrent first discovery of identical content at different paths, check repeated discovery and unchanged job revisions, and exercise historical pagination and path privacy. Filesystem regressions first reproduced incorrect cleanup completion and missing-file classification when a recorded file was replaced by a directory, then passed after the fix.

Worker diagnostics tests exercise progress, failure recovery, cancellation, monotonic duration and delay detection, wall-clock changes, owner authorization, and path privacy. A real cycle records all four stages; a failed discovery leaves earlier stage results available and returns a sanitized explanation.

Download tests verify EPUB/PDF attachment names and types, Unicode filenames, authorized reads, partial and unsatisfiable ranges, and conflicts for unsafe, missing, changed, or oversized files. Failed requests leave file bytes and import status intact. UI tests cover in-page errors, repeated clicks, and cancellation when leaving the view.

## Source coverage

Combined backend coverage is 2,249 of 2,401 executable source lines (93.7%), excluding generated migrations and `.g.cs` files. The unit and integration reports under `.tools/coverage-2a9cfa3f818041a083ba656e1b504d17` describe the same API source revision. This is line coverage, not combined branch coverage; subprocesses such as the packaged password helper are outside the managed test collector. `scripts/verify.ps1 -Coverage` collects both suites into a fresh directory and produces this combined summary in the same run.

Coverage analysis led to regressions for changed sources, exhausted retries, cleanup disabled after restart, concurrent cleanup claims, and session renewal after logout. `scripts/Measure-Coverage.ps1` unions report hits by file and line. Its fixture check verifies complementary hits, duplicate classes, generated-file exclusions, path separator normalization, and failure when reports are absent. CI is configured to publish that summary; a hosted CI run is still pending.

## Publication staging

PostgreSQL and real-file tests force a stale publisher to resume after recovery, both with and without maintenance removing its abandoned stage. They verify the winning book and bytes remain unchanged. Other cases cover failed plan commits, exclusive creation collisions, failed ownership callbacks, cancellation before promotion and after rename, failed completion commits, concurrent maintenance, locked Windows files, reparse directories, path mismatches, unknown files, and unconfirmed ownership. A failed maintenance save after deletion rolls back its journal update; replay settles the absent file while preserving the source and destination hashes.

An upgrade test removes the new migration from a seeded interrupted import, reapplies it, and recovers the null-fence row without removing a legacy temporary file. Diagnostics tests check owner access, no-store responses, correct counts, and absence of paths or internal errors. Component and desktop/mobile browser tests exercise counts, retry, stale data, polling cancellation, and expansion. The initial browser run exposed an ambiguous text selector; an exact selector fixed it and all ten journeys passed on rerun.

## Retained-source undo

Undo tests verify read-only previews, relative-path privacy, original retention, return to review, explicit reapproval, scanner identity, and replay of an old operation ID after a new book has been approved. Controlled failures cover request persistence before deletion, completion persistence after deletion, cancellation after durable admission, bounded retries, source restoration, changed or missing copies, directories, unsafe paths, and reparse points. Owner authentication and CSRF remain required.

Concurrency tests pause cleanup after its durable claim and force undo to win the deletion lock, then confirm the obsolete cleanup cannot remove the original. The reverse order preserves the library copy when cleanup has already removed the source. Another test holds undo admission before commit while a scan requests the shared advisory lock; the scan sees the committed undo and rejects overlapping admission.

A worker regression first reproduced a persistent undo completion failure blocking unrelated queued imports and later worker stages. Per-operation isolation now preserves the failing operation for recovery while allowing other work to advance.

The larger test suite exposed PostgreSQL client exhaustion from cached test-host connection pools. The factory now owns a shared data source and disposes it after its root and child hosts stop. Sync and async disposal regressions observe multiple live sessions, verify a disposed child leaves the parent usable, and then confirm zero database sessions remain after root shutdown. Independent migration and scanner contexts now share that owned source; all fourteen affected integration cases passed after that final adjustment.

## Scope and limits

Folder-profile checks cover prefix boundaries, native platform case rules, portable names, parsing orders, immutable snapshots after configuration removal, embedded metadata priority, duplicate ownership, migration defaults, and rejection of unsafe saved collection paths. Previewed and actual destinations match; undo and reapproval preserve routing. The packaged run configures a `fiction` watch subfolder and a `Fiction` library collection, verifies the captured profile, and follows that routing through restoration and browser undo/reapproval.

Larger generated-book benchmarks passed two files at 1 MiB and ten at 4 MiB of requested content per file. Both originals and library copies were hashed after staging settlement. A 100-tiny-file run settled all 100 journal rows in five batches with no partial files left. These are successful-publication reconciliation checks, not orphan-byte deletion benchmarks. See [performance](performance.md) for the measured values and local evidence.

- Browser tests use Chromium, including mobile viewport emulation; they do not establish compatibility with Safari or Firefox.
- Provider tests use controlled responses. Live metadata services, remote certificate provisioning, and public CA validation were not exercised.
- Storage checks cover temporary local files and Docker bind mounts. Network filesystems, power loss, and hostile concurrent filesystem changes are outside this evidence.
- Dependency audits are point-in-time results. GitHub Actions is configured, but no hosted CI run or published release is claimed here.
- The original-schema upgrade test is one supported migration path, not a complete upgrade matrix.
