# Changelog

## 0.2.0 — Unreleased

- Rebuilt ingestion around persisted claims, leases, revision checks, and bounded retries.
- Added verified publication without replacement and recoverable optional source cleanup.
- Replaced broken PDF extraction with PdfPig and retained metadata provenance.
- Added provider caching, shared requests, response limits, deadlines, and rate-limit cooldowns.
- Bounded repeated hashing of unchanged ingest files while retaining periodic revalidation.
- Added paginated duplicate-file observations without changing approval state or deleting copies.
- Rebuilt the review desk in TypeScript with search, paging, manual choices, previews, and history.
- Preserved unsaved metadata drafts during refresh and guarded against stale responses.
- Added resumable read-only integrity scans and a library health view.
- Corrected source cleanup and integrity status when a recorded file path becomes a directory.
- Corrected Docker persistence and API routing; added non-root containers and host/origin checks.
- Added owner sign-in, persisted revocable sessions, CSRF validation, password rotation, and sign-in rate limits.
- Added unit, PostgreSQL integration, component, browser, migration, and deployment/restore checks.
- Added project instructions, skills, CI, demo fixtures, and architecture documentation.

Imports now require explicit review, and originals are retained by default. Direct Calibre integration is not included.
