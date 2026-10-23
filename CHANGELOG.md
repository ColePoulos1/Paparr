# Changelog

## 0.2.0 — 2026-10-22

- Rebuilt ingestion around persisted claims, leases, revision checks, and bounded retries.
- Added verified publication without replacement and recoverable optional source cleanup.
- Fenced publication attempts against stale workers and added journaled temporary-file maintenance with owner-only diagnostics.
- Added retained-source undo with verified previews, durable recovery, operation-id replay protection, and coordination with source cleanup and integrity scans.
- Added folder profiles with captured collection routing and filename order, preserved through retries and undo.
- Replaced broken PDF extraction with PdfPig and retained metadata provenance.
- Added provider caching, shared requests, response limits, deadlines, and rate-limit cooldowns.
- Bounded repeated hashing of unchanged ingest files while retaining periodic revalidation.
- Added paginated duplicate-file observations without changing approval state or deleting copies.
- Rebuilt the review desk in TypeScript with search, paging, manual choices, previews, and history.
- Preserved unsaved metadata drafts during refresh and guarded against stale responses.
- Added edition identifiers with ISBN checksums, publisher, publication year, and language, including explicit manual choices.
- Rejected null characters before database persistence while preserving usable metadata and filename fallback.
- Generated frontend API contracts and added a drift check to local verification and CI.
- Added resumable read-only integrity scans and a library health view.
- Corrected source cleanup and integrity status when a recorded file path becomes a directory.
- Added owner-only worker diagnostics and a responsive stage status screen.
- Added verified library downloads, resumable API ranges, and download errors within the library view.
- Added verified Calibre export archives with XML-safe approved metadata and explicit handoff instructions.
- Corrected Docker persistence and API routing; added non-root containers and host/origin checks.
- Restricted forwarded HTTPS headers at both proxy hops and rejected invalid UI proxy CIDRs at startup.
- Added owner sign-in, persisted revocable sessions, CSRF validation, password rotation, and sign-in rate limits.
- Added unit, PostgreSQL integration, component, browser, migration, and deployment/restore checks.
- Added combined source-line coverage reporting and regressions for policy changes, extraction failures, and session renewal races.
- Added bounded larger PDF/EPUB benchmark fixtures, source hash verification, and staging journal settlement measurements.
- Added project instructions, skills, CI, demo fixtures, and architecture documentation.

Imports now require explicit review, and originals are retained by default. Paparr does not directly manage a Calibre database.
