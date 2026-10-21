---
name: paparr-bugfix
description: Reproduce and fix Paparr defects with focused regression coverage.
---

Read AGENTS.md. Trace the reported symptom through the API, worker, database, and filesystem as applicable. Construct the smallest failing regression using disposable data. Identify the violated invariant before changing code. Make the fix at the owning boundary and test relevant adjacent states, especially retry, repeated approval, and restart. Do not clear a database or delete a source library to repair a test. Explain the trigger, resulting behavior, and verification briefly.
