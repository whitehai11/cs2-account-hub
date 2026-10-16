# Steam Mail integration

The Steam Mail inbox is intentionally a global mailbox. It does not attempt to
associate messages with individual Steam accounts.

Configure the server with environment variables (or Docker secrets):

- `CS2_HUB_IMAP_HOST`
- `CS2_HUB_IMAP_PORT` (default `993`)
- `CS2_HUB_IMAP_USERNAME`
- `CS2_HUB_IMAP_PASSWORD`
- `CS2_HUB_IMAP_FOLDER` (default `INBOX`)
- `CS2_HUB_IMAP_SENDERS` (optional comma-separated sender/domain allowlist)
- `CS2_HUB_IMAP_SENDERS_SECONDARY` (optional comma-separated sender/domain allowlist for the second Mail area)
- `CS2_HUB_IMAP_PRIMARY_LABEL` (optional UI label, default `Steam`)
- `CS2_HUB_IMAP_SECONDARY_LABEL` (optional UI label, default `Zweite Domain`)
- `CS2_HUB_IMAP_SYNC_SECONDS` (default `300`, bounded to 60–3600)
- `CS2_HUB_IMAP_HOST_SECONDARY` (optional; falls back to primary host)
- `CS2_HUB_IMAP_PORT_SECONDARY` (optional; falls back to primary port)
- `CS2_HUB_IMAP_USERNAME_SECONDARY` (optional second IMAP login)
- `CS2_HUB_IMAP_PASSWORD_SECONDARY` (optional second IMAP password)
- `CS2_HUB_IMAP_FOLDER_SECONDARY` (optional; falls back to primary folder)

Only sender, subject, received date and bounded plain-text previews are stored.
HTML is converted to text and attachments are ignored. The app never sends
mail and never changes mailbox flags; read/unread state is local to the hub.
Allowed senders are classified into the primary and optional secondary UI area;
the secondary area can be backed by a separate IMAP login. If its host, port or
folder are omitted, the primary mailbox settings are reused. Messages that
match neither configured group are ignored. The list and message detail each
scroll inside their own panel so the page layout stays fixed while browsing a
large mailbox.
