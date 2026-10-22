# Development

Use the .NET SDK selected by `global.json`, Node 24, PowerShell 7, and Docker Desktop or Docker Engine. The Node version is recorded in `.node-version`.

The SDK accepts later .NET 10 feature bands so the local SDK and current .NET 10 container images can both build the project.

## Checks

```powershell
dotnet tool restore
cd src/Paparr.UI
npm ci
npx playwright install chromium
cd ../..
pwsh scripts/verify.ps1
```

The verification script stops at a failing command. `-SkipIntegration` and `-SkipBrowser` explicitly omit those suites and print a warning. PostgreSQL integration tests create disposable databases and files; they do not use the development connection string or personal ingest directory.

Backend tests:
```sh
dotnet test tests/Paparr.UnitTests
dotnet test tests/Paparr.IntegrationTests
```

Frontend tests:
```sh
cd src/Paparr.UI
npm run lint
npm run api:check
npm test
npm run build
npm run test:e2e
```

Browser tests exercise the UI against controlled HTTP responses. PostgreSQL integration tests separately exercise the real API, database, worker, and filesystem. Run `pwsh scripts/Test-Deployment.ps1` for the packaged application and backup restoration. It uses synthetic files, a unique Compose project, and port 5188 by default. `-KeepRunning` leaves its demo available; otherwise it removes its own containers, images, and database volume while retaining the local evidence files under `.tools`.

Add `-Browser` to exercise the packaged UI against its real API on desktop and mobile. This requires the UI's installed dependencies and Playwright Chromium. CI runs this mode and saves screenshots on completion or failure.

`pwsh scripts/Test-ReleaseBundle.ps1` checks source packaging with a disposable Git repository, including dirty-tree rejection, deterministic archives, hashes, and excluded local data. After committing a verified checkpoint, create a source bundle with `pwsh scripts/New-ReleaseBundle.ps1 -Destination .tools/release-checkpoint`. Create `.tools` first if it does not exist. The destination must be new; the script packages the exact clean `HEAD` and refuses tracked environment files, runtime data, links, and submodules. Its manifest records the commit and archive hash. It produces source for a local build, not signed binaries or published images.

## Coverage

Collect backend coverage into a fresh results directory, then combine source-line hits:

```powershell
dotnet test Paparr.slnx --configuration Release --collect:"XPlat Code Coverage" --results-directory .tools/coverage-run
pwsh scripts/Measure-Coverage.ps1 -ResultsDirectory .tools/coverage-run
```

Use a new directory for each source revision. The report excludes generated migrations and `.g.cs` files, merges unit/integration hits without double-counting source lines, and writes `.tools/backend-coverage.md`. It does not calculate combined branch coverage or establish correctness. CI publishes the same summary alongside the original reports.

## Running locally

Start a local PostgreSQL database, set `ConnectionStrings__DefaultConnection`, and set absolute `IngestPath` and `LibraryPath` values. Keep the two directories separate. Alternatively set `Database__Host`, `Database__Name`, `Database__User`, and `Database__Password`; no password is supplied by default.

Run `dotnet run --project src/Paparr.API -- --hash-password` to generate the owner hash, then set `Authentication__PasswordHash`. For a trusted local test only, explicitly set `Authentication__Enabled=false`. Data Protection keys default to the ignored `.paparr/keys` directory. Integration tests use isolated keys and databases.

```sh
dotnet run --project src/Paparr.API
```

In another terminal:
```sh
cd src/Paparr.UI
npm run dev
```

The UI at http://localhost:5173 proxies API requests to http://localhost:5000. Swagger is available at http://localhost:5000/swagger in Development. Production uses the same-origin Nginx proxy.

## API contracts

Run `npm run api:generate` from `src/Paparr.UI` after changing a request or response model, then commit `src/generated`. `npm run api:check` and the verification script reject stale generated files. Generation uses the Release API's OpenAPI export without starting the host, connecting to PostgreSQL, running migrations, or accessing configured libraries. No running API or owner credentials are needed.

The UI derives its wire shapes and domain status values from these files. A small worker display-state union remains local. Request constructor validation, enum names, and explicit nullable response fields are covered by contract tests. Generated types check compile-time use; they do not validate arbitrary JSON at runtime.

## Migrations

```sh
dotnet ef migrations add DescriptiveName --project src/Paparr.API -- --environment Development
```

Review the generated SQL and run integration tests. The API applies migrations at startup by default. Set `Database__ApplyMigrations=false` when migrations are managed separately. Existing duplicate content hashes must be reconciled before applying the unique-hash index; migration failure does not authorize deleting those imports.

See [architecture](docs/architecture.md) for import invariants and [overhaul progress](docs/overhaul.md) for remaining work.
