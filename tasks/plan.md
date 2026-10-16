# Implementation Plan: Fleet History and Operations

## Overview

Move the existing account timeline into its own sidebar area and extend the hub with status/ban history, bulk account operations, event-driven Discord alerts, fleet analytics, and safe export/backup flows. Automatic recovery remains semi-automatic: refresh tokens and Steam Guard sources may be used, but passwords are entered only for the active login and are not stored.

## Architecture Decisions

- Reuse `AccountEventStore` as the canonical event history instead of introducing a second history database.
- Record meaningful state transitions and deduplicate repeated scheduler observations.
- Keep bulk actions on the existing `OperationQueue`; each account receives its own operation and result.
- Keep Discord notifications transition-based and rate-limited by persisted account flags where necessary.
- Export excludes refresh tokens, protected secrets, MAFile secrets, and Data Protection keys. Full restore backups must remain an explicitly protected server-side operation.
- Render charts from bounded analytics responses and not from unbounded raw event payloads.

## Task List

### Phase 1: History foundation and navigation

- [ ] Task 1: Move Account-Timeline into a dedicated sidebar view and remove it from Personalize.
- [ ] Task 2: Record and expose meaningful ban/status transitions in the existing event store.

### Checkpoint: History

- [ ] Existing tests pass.
- [ ] Timeline opens independently and account detail links still work.
- [ ] Production build succeeds.

### Phase 2: Fleet operations

- [ ] Task 3: Add multi-select account rows and a bulk-action toolbar.
- [ ] Task 4: Add a batch-command API that queues one safe operation per selected account.

### Checkpoint: Bulk actions

- [ ] Empty selection, invalid accounts, duplicate clicks, and per-account failures are handled.
- [ ] Bulk progress is visible in the UI.

### Phase 3: Alerts and analytics

- [ ] Task 5: Add transition-based Smart Alerts for bans, login state, GC state, and cooldown changes.
- [ ] Task 6: Add bounded dashboard analytics and charts backed by persisted history.

### Phase 4: Export and backup

- [ ] Task 7: Add redacted CSV/JSON export for accounts and history.
- [ ] Task 8: Add an explicitly protected server backup flow with clear key-handling warnings.

### Checkpoint: Complete

- [ ] Full test suite passes.
- [ ] Docker production build and health check pass.
- [ ] Browser smoke test covers navigation, selection, bulk action feedback, charts, and export.
- [ ] No credentials, refresh tokens, MAFile secrets, or Data Protection keys appear in exports.

## Risks and Mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Scheduler emits repeated identical events | High event noise and Discord spam | Compare persisted state before appending or notifying; cap history size |
| Bulk refresh hits Steam rate limits | Medium/High | Queue operations, limit concurrency, show per-account results |
| Export leaks authentication material | Critical | Dedicated redacted DTOs and tests that assert sensitive fields are absent |
| Backup omits `data/keys` | High, imported secrets become unreadable | Make key inclusion explicit and warn before restore/download |
| Existing single-account flows regress | High | Keep the existing operation queue and run the full suite after each slice |

## Open Questions

- Automatic recovery is intentionally not fully unattended until a password-storage policy is explicitly chosen.
