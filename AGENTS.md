# Working on Paparr

Paparr imports EPUB and PDF files into a personal library. Keep imports recoverable and metadata decisions understandable.

## Layout

- `src/Paparr.API`: ASP.NET Core API, PostgreSQL persistence, metadata adapters, and ingestion worker.
- `src/Paparr.UI`: React application.
- `tests`: backend unit and PostgreSQL integration tests.
- `scripts/verify.ps1`: local checks matching CI.
- `docs`: architecture decisions, development instructions, and overhaul progress.
- `.agents/skills`: project workflows for verification, features, tests, bug fixes, and releases.

## Commands

- `dotnet build Paparr.slnx`
- `dotnet test tests/Paparr.UnitTests`
- `dotnet test tests/Paparr.IntegrationTests` (Docker required)
- From `src/Paparr.UI`: `npm ci`, `npm run lint`, `npm run test`, `npm run build`.
- `pwsh scripts/verify.ps1` runs the complete local verification suite.

## Invariants

- Never overwrite a library file. Publish through a temporary file on the destination filesystem and verify its hash.
- Keep the source until the destination and database record are durable. Failed cleanup must not turn a completed import into a failed import.
- Check filesystem containment and reject symbolic links/reparse points. Metadata is untrusted input.
- Persist work before executing it. Approval, retries, and worker recovery must tolerate repeated requests.
- Database constraints and concurrency tokens enforce ownership; an in-process lock alone is insufficient.
- External metadata services can fail independently. Tests use controlled HTTP responses, not live providers.
- Tests and demos use temporary directories and disposable databases, never configured personal libraries.

## Implementation

Keep controllers thin and isolate matching, naming, and transition rules from I/O. Prefer small boundaries around external effects over generic repositories. Pass cancellation through asynchronous I/O. Add migrations for model changes; do not reset existing data to make a migration pass.

For a bug, reproduce the observable failure with a regression test. Test database behavior against PostgreSQL. Use real temporary files for filesystem behavior and controlled failure injection at persistence boundaries. Report checks that could not run.

Comments explain constraints and non-obvious decisions. Avoid narrating code, repetitive XML summaries, speculative advice, and ornamental documentation. Keep user-facing errors actionable without exposing server paths or credentials.
