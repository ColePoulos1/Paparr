---
name: paparr-verify
description: Verify Paparr changes using the repository checks and report concrete evidence.
---

Read AGENTS.md and scripts/verify.ps1. Select checks from the changed behavior: backend rules require unit tests; persistence, worker, and endpoint changes require PostgreSQL integration tests; UI changes require lint, component tests, build, and affected browser journeys. Run the complete script before a release. Docker unavailability is a blocked integration check, not a passing suite. Use isolated test configuration; never start the worker against a personal ingest path. Report command outcomes, relevant test counts, and remaining verification gaps. Do not repeatedly rerun passing checks without a new change or unresolved concern.
