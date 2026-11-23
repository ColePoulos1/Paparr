# Astryx frontend overhaul

Paparr’s owner should be able to move from a watch-folder import to a verified library file, understand a metadata choice, and recover from an interruption. Preserve these workflows, their URLs, and their API contracts throughout the redesign.

| Work | Owner | Checkpoint |
| --- | --- | --- |
| Install official Astryx core, Matcha theme and local tooling. Replace the walnut ledger rules and Impeccable setup. | Integration | Integrated. Astryx doctor passed. |
| Rebuild navigation, queue selection, search, counts, pagination and empty states. | Workspace | Implemented. The mobile browser test covers access to a selected import in a 25-item queue. |
| Migrate review, metadata choices, destination preview, duplicates, download and undo. | Review | Implemented. Browser tests cover keyboard input, approval and undo. |
| Migrate owner sign-in, library audits, folder profiles, staging and worker status. | Operations | Implemented. Browser tests cover sign-in, audit and worker status. |
| Verify the production build, unit and browser workflows, inspect desktop and narrow layouts, and update the README screenshot. | Integration | Complete. Frontend and packaged Docker checks passed. |

Use the shipped theme, typography, components and semantic color tokens. Keep custom CSS for Paparr’s responsive work areas. Give the queue and selected import distinct regions on wide screens and a single reading order on narrow screens. Keep feedback near the action and preserve native keyboard and form behavior.

## Verification

- On Node 24, lint, the production build, 58 component and app tests, and 13 desktop and mobile Chromium scenarios passed. The full-queue navigation case runs only in the mobile project.
- `pwsh scripts/Test-Deployment.ps1 -Port 5199 -Browser` passed all 12 deployment checks in a disposable Docker Compose project. It exercised a clean install, backup restoration, verified downloads and Calibre export creation, and signed-in desktop and mobile workflows through approval, undo and reapproval.
- Astryx reported 159 component uses and confirmed that the production build includes Core styles. Its advisory flags custom layout CSS and native markup. Paparr uses those for page layout, tabular data and native form semantics.
- The documentation screenshots come from the production build with fixture data. Desktop and narrow layouts were checked under the Nginx content security policy. The review workspace was also checked in dark mode.
- The production build reports a 949 kB entry chunk before gzip and 302 kB after gzip. npm audit reported nine moderate findings in the pre-existing ESLint development dependency chain.
