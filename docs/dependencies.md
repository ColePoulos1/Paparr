# Dependency decisions

PDF metadata extraction uses [PdfPig](https://github.com/UglyToad/PdfPig), licensed under Apache 2.0. The former PDFsharp dependency was unused by the original ZIP-based extraction. PDFsharp 6.5 introduced a license activation requirement; adopting PdfPig keeps parsing independent of that activation.

Backend integration tests use PostgreSQL 18 through Testcontainers and ASP.NET Core WebApplicationFactory. Unit tests do not need Docker. Browser tests use Playwright; component tests use Vitest and Testing Library.

Versions are recorded in the project files and npm lockfile. CI and local verification use the same toolchain. Dependency updates must pass behavior tests; a successful restore is insufficient evidence of compatibility.

API contracts use the pinned `@hey-api/openapi-ts` TypeScript plugin against a locally exported OpenAPI document. Its parser pins an older `js-yaml`; a scoped override selects patched 4.3.2 within the same major version. Keep the override until the parser updates its dependency, and run generation, contract checks, and the dependency audit when changing it.
