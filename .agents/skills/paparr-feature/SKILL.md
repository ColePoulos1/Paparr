---
name: paparr-feature
description: Build a Paparr feature across its domain rules, API, persistence, UI, and tests.
---

Read AGENTS.md and the relevant milestone in docs/overhaul.md. Define observable acceptance criteria and failure behavior before editing. Trace the feature through persisted state, HTTP contracts, and the review workflow. Preserve import safety and idempotency. Add a migration for model changes, validate external inputs at boundaries, and expose useful errors without server paths. Keep preview and execution on the same decision logic. Test behavior at the smallest appropriate layer and add integration coverage for persistence or concurrency. Update user documentation only for behavior actually shipped.
