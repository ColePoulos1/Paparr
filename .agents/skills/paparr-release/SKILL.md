---
name: paparr-release
description: Verify a Paparr release across installation, migration, recovery, and documentation.
---

Read AGENTS.md, docs/overhaul.md, and deployment instructions. Run scripts/verify.ps1 and the documented Compose smoke checks in an isolated project. Verify a clean install, migration from the previous schema, container recreation with persistent data, and backup restoration. Check dependency advisories and distinguish runtime exposure from tooling. Compare README claims with executed behavior and record limitations. Prepare a concise changelog and demo using synthetic fixtures. Publishing images or tags is a separate external action and requires authorization from the current task.
