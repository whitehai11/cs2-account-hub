# Capability Map: CS2 Account Hub refresh

| Module | Responsibility | Depends on |
|---|---|---|
| `dashboard-ui` | Rework the existing browser dashboard shell, account list, inspector, settings views, and responsive states. | Existing backend contracts |
| `steam-mail` | Read Steam messages from a configured IMAP mailbox, persist safe metadata, and expose a global inbox. | Existing session/auth middleware; dashboard UI |

Build order: `dashboard-ui` → `steam-mail` backend contract → `steam-mail` UI.

The mailbox is global. Messages are deliberately not assigned to individual Steam accounts.
