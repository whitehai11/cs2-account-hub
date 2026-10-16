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
- `CS2_HUB_IMAP_SYNC_SECONDS` (default `300`, bounded to 60–3600)

Only sender, subject, received date and bounded plain-text previews are stored.
HTML is converted to text and attachments are ignored. The app never sends
mail and never changes mailbox flags; read/unread state is local to the hub.
