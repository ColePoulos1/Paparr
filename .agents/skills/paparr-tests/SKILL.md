---
name: paparr-tests
description: Create meaningful Paparr unit, integration, parser, and UI regression tests.
---

Read AGENTS.md and nearby tests. Unit-test pure matching, naming, and transition rules. Exercise PostgreSQL constraints and queries with the integration fixture, never EF's in-memory provider. Generate small valid EPUB/PDF fixtures with controlled metadata; keep corrupt fixtures intentional. Use temporary filesystem roots and deterministic clocks. Replace provider HTTP responses, not the application behavior under test. For import safety, verify both persisted state and file bytes after failure. Include repeat requests and interruption boundaries when changing publication. Avoid snapshots of implementation details, arbitrary sleeps, and tests that merely restate constants.
