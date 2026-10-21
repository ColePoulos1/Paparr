# Verification record

## October 21, 2026

Executed locally on Windows with .NET SDK 10.0.301/runtime 10.0.9, Node 24, Docker Desktop, and PostgreSQL 18.6 containers. Fixtures and databases were disposable; no personal library was used.

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings and errors |
| Backend unit tests | 70 passed |
| PostgreSQL integration tests | 29 passed |
| React component tests | 23 passed |
| Desktop/mobile browser tests with controlled API responses | 8 passed |
| Frontend lint and production build | Passed |
| NuGet transitive dependency audit | No known vulnerabilities reported |
| Clean Docker frontend dependency installation | No known vulnerabilities reported |

The main suites ran through `pwsh scripts/verify.ps1 -SkipIntegration`, followed by `dotnet test tests/Paparr.IntegrationTests --configuration Release --no-build` once Docker was available. This covers the same checks as the complete verification script.

Integration tests exercise real PostgreSQL migrations and constraints, concurrent approvals, publication commit failures and recovery, source cleanup interruption, integrity scan recovery, revocable owner sessions, CSRF validation, password rotation, and trusted proxy handling. An upgrade test seeds the original schema before applying current migrations and checks that its import and book survive.

`pwsh scripts/Test-Deployment.ps1 -Browser` passed all seven packaged checks in a fresh Compose project: installation and extraction, verified publication with source retention, container recreation, read-only integrity checks, database/library restoration into separate locations, session persistence and revocation, and desktop/mobile browser review. The browser run edited missing metadata, imported that book, scanned both library files, displayed a failed import, and confirmed a copied session stopped working after sign-out. No browser page errors or page-wide horizontal overflow were observed. The script removed its own containers, volumes, and images afterward.

Local evidence is saved under `.tools/paparr-smoke-77d4fee59012/results.json` and its `browser` directory; these disposable artifacts are intentionally ignored by Git.

The [discovery hashing benchmark](performance.md) measured 83% fewer bytes hashed across six passes over unchanged files. Its local timing is a microbenchmark, not an ingestion throughput claim.

## Scope and limits

- Browser tests use Chromium, including mobile viewport emulation; they do not establish compatibility with Safari or Firefox.
- Provider tests use controlled responses. Live metadata services and remote HTTPS termination were not exercised.
- Storage checks cover temporary local files and Docker bind mounts. Network filesystems, power loss, and hostile concurrent filesystem changes are outside this evidence.
- Dependency audits are point-in-time results. GitHub Actions is configured, but no hosted CI run or published release is claimed here.
- The original-schema upgrade test is one supported migration path, not a complete upgrade matrix.
