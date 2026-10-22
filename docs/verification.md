# Verification record

## October 21, 2026

Executed locally on Windows with .NET SDK 10.0.301/runtime 10.0.9, Node 24, Docker Desktop, and PostgreSQL 18.6 containers. Fixtures and databases were disposable; no personal library was used.

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings and errors |
| Backend unit tests | 99 passed |
| PostgreSQL integration tests | 72 passed |
| React component tests | 47 passed |
| Desktop/mobile browser tests with controlled API responses | 10 passed |
| Frontend lint and production build | Passed |
| NuGet transitive dependency audit | No known vulnerabilities reported |
| Clean Docker frontend dependency installation | No known vulnerabilities reported |

The current suites passed 228 tests in total, with lint, production builds, generated-contract checks, and the coverage/release script fixtures. API schema tests cover response nullability, request constructor validation, enum values, and path privacy. Deliberately changing a generated contract caused the drift check to fail; restoring it passed.

Integration tests exercise real PostgreSQL migrations and constraints, concurrent approvals, publication commit failures and recovery, source cleanup interruption, integrity scan recovery, revocable owner sessions, CSRF validation, password rotation, and trusted proxy handling. An upgrade test seeds the original schema before applying current migrations and checks that its import and book survive.

`pwsh scripts/Test-Deployment.ps1 -Browser -Calibre` passed all twelve packaged checks in a fresh Compose project: installation and extraction, duplicate discovery and retention, verified publication with source retention, container recreation, read-only integrity checks, database/library restoration into separate locations, owner-only verified downloads and Calibre archives, session persistence and revocation, desktop/mobile browser review, and actual Calibre imports into isolated libraries. The browser run edited missing metadata including ISBN, publisher, year, and language, confirmed approval committed, scanned both library files, downloaded matching original bytes, exported approved metadata, inspected worker stages, displayed a failed import, and confirmed a copied session stopped working after sign-out. Calibre 5.44 preserved the approved title, author, ISBN, publisher, year, and language from the browser export. No browser page errors or page-wide horizontal overflow were observed. The script removed its own containers, volumes, and images afterward.

The latest packaged run includes staging diagnostics on desktop and mobile. Evidence is saved under `.tools/paparr-smoke-d3e3d62879de/results.json` and its `browser` directory; these disposable artifacts are intentionally ignored by Git. The worker screenshots were inspected and updated. Earlier visual inspection prompted radio-alignment and action-spacing fixes, followed by frontend verification and a rebuilt demo check.

The retained synthetic demo at `http://127.0.0.1:5187` was rebuilt before the staging checkpoint. Its existing library survived that update, and a separate browser check passed owner sign-in, a matching-hash download, desktop/mobile worker status, and sign-out. This demo uses only generated books and its own database and mounts.

The [performance harnesses](performance.md) measured 83% fewer bytes hashed across six passes over unchanged files and exercised the complete offline import pipeline on ten small PDF/EPUB fixtures. These are bounded local observations, not sustained throughput claims.

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

Combined backend coverage is 1,925 of 2,066 executable source lines (93.2%), excluding generated migrations and `.g.cs` files. The unit and integration reports under `.tools/coverage-staging` describe the same API source revision. This is line coverage, not combined branch coverage; subprocesses such as the packaged password helper are outside the managed test collector.

Coverage analysis led to regressions for changed sources, exhausted retries, cleanup disabled after restart, concurrent cleanup claims, and session renewal after logout. `scripts/Measure-Coverage.ps1` unions report hits by file and line. Its fixture check verifies complementary hits, duplicate classes, generated-file exclusions, path separator normalization, and failure when reports are absent. CI is configured to publish that summary; a hosted CI run is still pending.

## Publication staging

PostgreSQL and real-file tests force a stale publisher to resume after recovery, both with and without maintenance removing its abandoned stage. They verify the winning book and bytes remain unchanged. Other cases cover failed plan commits, exclusive creation collisions, failed ownership callbacks, cancellation before promotion and after rename, failed completion commits, concurrent maintenance, locked Windows files, reparse directories, path mismatches, unknown files, and unconfirmed ownership. A failed maintenance save after deletion rolls back its journal update; replay settles the absent file while preserving the source and destination hashes.

An upgrade test removes the new migration from a seeded interrupted import, reapplies it, and recovers the null-fence row without removing a legacy temporary file. Diagnostics tests check owner access, no-store responses, correct counts, and absence of paths or internal errors. Component and desktop/mobile browser tests exercise counts, retry, stale data, polling cancellation, and expansion. The initial browser run exposed an ambiguous text selector; an exact selector fixed it and all ten journeys passed on rerun.

## Scope and limits

- Browser tests use Chromium, including mobile viewport emulation; they do not establish compatibility with Safari or Firefox.
- Provider tests use controlled responses. Live metadata services, remote certificate provisioning, and public CA validation were not exercised.
- Storage checks cover temporary local files and Docker bind mounts. Network filesystems, power loss, and hostile concurrent filesystem changes are outside this evidence.
- Dependency audits are point-in-time results. GitHub Actions is configured, but no hosted CI run or published release is claimed here.
- The original-schema upgrade test is one supported migration path, not a complete upgrade matrix.
