# CS2 Account Hub

Current release: `0.2.0`

Server-first CS2 account dashboard for Ubuntu/Docker with a browser frontend.

The server owns the dashboard login, Steam authentication, Steam Guard code flow, CS2 Game Coordinator status queries, Prime classification, cooldown/VAC sorting, Loadout, Personalize and Workshop actions. The browser is the frontend; Steam credentials and Guard codes are entered there and handled server-side in memory. No Steam desktop client or AutoExec component is required.

## Project layout

- `src/Shared`: DTOs and enums shared by all components.
- `src/Backend`: ASP.NET Core API, encrypted token storage and sequential operation worker.
- `src/WindowsBridge`: legacy local Steam-client handoff for the older desktop client.
- `src/Desktop`: legacy WinUI 3 frontend; the current UI is served by the backend at the public API domain.
- `Dockerfile` and `docker-compose.yml`: Ubuntu server deployment.

Accounts are persisted in `/data`, while refresh tokens are protected with ASP.NET Data Protection keys also stored in `/data/keys`. Keep the Docker volume and API key private. Passwords and Steam Guard codes are accepted only in memory during the authentication request and are never written to the account database or logs.

The dashboard uses a four-hour, `HttpOnly`, `Secure`, `SameSite=Strict` session cookie. Sessions are invalidated automatically when the configured dashboard username or password changes. The API has request-size limits, per-IP rate limits, strict security headers, no cross-origin browser access, and does not expose refresh tokens or the legacy local Steam handoff.

The classification is deliberately conservative: an account is `PlayReady` only when the GC returned cooldown data, VAC is explicitly clear and no cooldown is active. Unknown status stays `NotPlayReady` until a refresh confirms it.

## Build

The repository needs the .NET 8 SDK for development. The current Codex host only has the .NET runtime installed, so compilation must be done on a machine or CI runner with the SDK, or through Docker.

```powershell
dotnet build .\CS2AccountHub.sln
```

```bash
cat > .env <<'EOF'
CS2_HUB_API_KEY=replace-with-a-long-random-value
CS2_HUB_WEB_USERNAME=admin
CS2_HUB_WEB_PASSWORD=replace-with-a-long-dashboard-password
# Optional global Steam Mail inbox (IMAP over TLS; never put these in the browser)
CS2_HUB_IMAP_HOST=imap.example.com
CS2_HUB_IMAP_USERNAME=steam@example.com
CS2_HUB_IMAP_PASSWORD=replace-with-mail-password
# Optional: comma-separated exact addresses/domains; defaults to Steam domains
CS2_HUB_IMAP_SENDERS=steampowered.com,steamcommunity.com
# Optional allowlist for automatic Steam Guard code lookup; when empty, the
# domains of the configured IMAP mailbox usernames are used automatically.
CS2_HUB_STEAM_GUARD_DOMAINS=first-domain.example,second-domain.example
# Optional second sender/domain group shown as a separate Mail area
CS2_HUB_IMAP_SENDERS_SECONDARY=example.org
CS2_HUB_IMAP_PRIMARY_LABEL=Steam
CS2_HUB_IMAP_SECONDARY_LABEL=Zweite Domain
# Optional second IMAP mailbox; host/port/folder fall back to the primary mailbox
CS2_HUB_IMAP_USERNAME_SECONDARY=webmail@example.org
CS2_HUB_IMAP_PASSWORD_SECONDARY=replace-with-second-mail-password
EOF
chmod 600 .env
docker compose up --build -d
```

The compose file expects the external Docker network `npm_default` used by Nginx Proxy Manager and publishes the backend on host port `18080`. Create the network once if it does not exist:

```bash
docker network create npm_default
```

Nginx Proxy Manager should proxy to `backend:8080` on the `npm_default` network. Port `18080` remains available on localhost for diagnostics but is no longer published to the public network. Keep HTTPS enabled at the proxy; the dashboard cookie is intentionally HTTPS-only.

Never commit real Steam passwords, Steam Guard codes, refresh tokens, or production encryption keys.

Open `https://cs2api.infra-maro.com` after the container is running and use `CS2_HUB_WEB_USERNAME` / `CS2_HUB_WEB_PASSWORD` for the dashboard. The API key remains available for compatible automation and the legacy desktop client; the browser uses the protected web session cookie instead. Steam accounts are added from the web dashboard with their Steam username/password, followed by manual Steam Guard or e-mail confirmation when requested.

Use new random values when rotating access: edit `.env`, then run `docker compose up -d --force-recreate`. Changing the dashboard password invalidates existing web sessions; changing the API key immediately invalidates API-key clients. Do not put either value in GitHub issues, browser URLs, logs or screenshots.

The browser dashboard contains Play Ready and Not Play Ready account lists, Prime filters, status refresh, Workshop clearing, server-side Loadout and Personalize presets, account notes and operation confirmations. AutoExec is intentionally not part of the application.

Cooldown notifications are optional. Set `CS2_HUB_DISCORD_WEBHOOK_URL` only in the server's `.env`; the worker sends one notification when the estimated cooldown end is reached and persists the sent state. Never commit or paste a webhook URL into GitHub or chat. If a webhook URL is exposed, revoke it in Discord immediately and create a new one.

Status checks are staggered per account by default. `CS2_HUB_CHECK_INTERVAL_MINUTES` controls the base interval (default `30`, allowed range `5` to `1440`); each account receives a stable time offset and checks run through one server-side queue to avoid CM bursts and unnecessary 429 responses.

The dashboard keeps the observed CS2 cooldown progression and shows the projected next duration: 20 hours, 7 days, 31 days, 90 days, then 181 days repeatedly. Recent CS2 matches can be loaded on demand from the Game Coordinator in the selected account's detail view.

The dashboard also includes an optional global Steam Mail inbox. It uses IMAP over TLS, never SMTP, does not map messages to accounts, stores bounded metadata and plain-text previews locally, and is disabled safely when the IMAP variables are missing. Read/unread state is local to the hub; attachments and raw HTML are not exposed. Messages can be routed into two independent UI areas: the primary Steam mailbox and an optional secondary mailbox/domain. The secondary host, port and folder fall back to the primary settings; its username and password are configured separately. See `src/Backend/Mail/README.md` for the full configuration.

## CI and releases

Every push and pull request builds the backend, Windows bridge and desktop client. Pushing a version tag such as `v0.2.0` creates a GitHub Release containing self-contained Windows desktop/bridge archives and a compressed Docker image archive for the backend. See [CHANGELOG.md](CHANGELOG.md) for the release history.

The Steam/CS2 adapter is based on the MIT-licensed `fakearchie/nfatool` reference. See `THIRD_PARTY_NOTICES.md` for attribution.
