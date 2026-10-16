# Spec: Global Steam Mail inbox

## Objective

Show messages from one configured mailbox inside the authenticated dashboard. The inbox is global and does not attempt to associate messages with individual Steam accounts. Allowed senders are separated into a primary Steam area and an optional secondary sender/domain area.

## Scope

- Read messages through IMAP over TLS; SMTP is out of scope.
- Use a background worker for periodic synchronization with a manual sync endpoint.
- Filter using explicit, configurable sender/domain allowlists and classify matches into two UI groups.
- Persist only bounded message metadata and a sanitized plain-text preview by default.
- Expose a global `Steam Mail` page with two group tabs, list-local scrolling, search, unread state, last-sync state, and message detail.
- Show untrusted email content as text; never inject raw email HTML into the page.
- Use safe defaults when IMAP is not configured: the application still starts and the page reports `Nicht konfiguriert`.

## Proposed Contract

```text
GET  /api/mail?query=&unread=&group=&limit=&before=
GET  /api/mail/{id}
POST /api/mail/sync
PATCH /api/mail/{id}/read
```

List responses are bounded and return stable camelCase fields: `id`, `subject`, `sender`, `receivedUtc`, `isRead`, `preview`, `hasBody`, and `groupId`. The list response also includes `groups` with counts for the primary and optional secondary areas.

## Configuration

Use environment variables or Docker secrets, never browser input or committed files:

- `CS2_HUB_IMAP_HOST`
- `CS2_HUB_IMAP_PORT` (default `993`)
- `CS2_HUB_IMAP_USERNAME`
- `CS2_HUB_IMAP_PASSWORD`
- `CS2_HUB_IMAP_FOLDER` (default `INBOX`)
- `CS2_HUB_IMAP_SENDERS` (explicit comma-separated allowlist)
- `CS2_HUB_IMAP_SENDERS_SECONDARY` (optional comma-separated allowlist for the second area)
- `CS2_HUB_IMAP_PRIMARY_LABEL` (optional UI label, default `Steam`)
- `CS2_HUB_IMAP_SECONDARY_LABEL` (optional UI label, default `Zweite Domain`)
- `CS2_HUB_IMAP_SYNC_SECONDS` (bounded safe interval)
- `CS2_HUB_STEAM_GUARD_DOMAINS` (optional comma-separated recipient-domain allowlist for automatic Steam Guard lookup; defaults to the configured IMAP mailbox domains)
- `CS2_HUB_IMAP_HOST_SECONDARY`, `CS2_HUB_IMAP_PORT_SECONDARY`, `CS2_HUB_IMAP_FOLDER_SECONDARY` (optional overrides; default to primary mailbox settings)
- `CS2_HUB_IMAP_USERNAME_SECONDARY`, `CS2_HUB_IMAP_PASSWORD_SECONDARY` (optional second IMAP login)

No account email address or Steam account mapping is stored.

## Security and Privacy Boundaries

- Require TLS and certificate validation for IMAP.
- Treat sender, subject, body, links, and attachments as untrusted external data.
- Do not download or expose attachments in the first version.
- Sanitize/convert HTML to text; never render remote HTML directly.
- Do not log mailbox credentials, message bodies, authentication codes, or full login links.
- Apply a bounded retention policy and cap stored preview/body sizes.
- Authenticate all mail endpoints with the existing web session/API-key guard.
- Do not accept an arbitrary IMAP host from the browser; configuration is server-side only.

## Testing Strategy

- Unit-test sender filtering, message normalization, truncation, and HTML-to-text handling with hostile input.
- Test disabled/unconfigured IMAP behavior without requiring a live mailbox.
- Test group classification, pagination/limits and unread mutations through the API contract.
- Test the UI with fixture messages containing HTML, scripts, long subjects, and missing headers.
- Run package vulnerability checks after adding the IMAP dependency.

## Boundaries

- Always: use TLS, bounded fetches, input validation, sanitized output, and generic external-service errors.
- Ask first: storing attachments, sending mail, changing mailbox contents beyond read flags, or adding OAuth/account-management flows.
- Never: commit mail credentials, render raw email HTML, or make account association guesses.

## Success Criteria

- With IMAP disabled, the app builds and the dashboard remains functional.
- With IMAP configured, new allowed messages appear in the correct area after sync and can be read in the UI.
- Non-allowed senders never appear in either Mail area.
- Search and synchronization report their real state, and message scrolling stays inside the list panel.
- Message content cannot execute scripts or create external page content in the dashboard.
- No mail message is assigned to a Steam account.

## Open Questions

- Exact sender addresses/domains to use in the initial primary and secondary allowlists.
- Desired retention period; proposed default is 30 days for locally stored previews.
- Whether read/unread state should be local-only or synchronized back to the IMAP mailbox; proposed default is local-only.
