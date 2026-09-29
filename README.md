# CS2 Account Hub

Server-first CS2 account dashboard for Ubuntu/Docker with a browser frontend.

The server owns the dashboard login, Steam authentication, Steam Guard code flow, CS2 Game Coordinator status queries, Prime classification, cooldown/VAC sorting, Loadout, Personalize and Workshop actions. The browser is the frontend; Steam credentials and Guard codes are entered there and handled server-side in memory. No Steam desktop client or AutoExec component is required.

## Project layout

- `src/Shared`: DTOs and enums shared by all components.
- `src/Backend`: ASP.NET Core API, encrypted token storage and sequential operation worker.
- `src/WindowsBridge`: legacy local Steam-client handoff for the older desktop client.
- `src/Desktop`: legacy WinUI 3 frontend; the current UI is served by the backend at the public API domain.
- `Dockerfile` and `docker-compose.yml`: Ubuntu server deployment.

Accounts are persisted in `/data`, while refresh tokens are protected with ASP.NET Data Protection keys also stored in `/data/keys`. Keep the Docker volume and API key private. Passwords and Steam Guard codes are accepted only in memory during the authentication request and are never written to the account database or logs.

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
EOF
docker compose up --build -d
```

The compose file expects the external Docker network `npm_default` used by Nginx Proxy Manager and publishes the backend on host port `18080`. Create the network once if it does not exist:

```bash
docker network create npm_default
```

Nginx Proxy Manager can then proxy to `backend:8080` on the `npm_default` network, or to the host on port `18080`.

Never commit real Steam passwords, Steam Guard codes, refresh tokens, or production encryption keys.

Open `https://cs2api.infra-maro.com` after the container is running and use `CS2_HUB_WEB_USERNAME` / `CS2_HUB_WEB_PASSWORD` for the dashboard. The API key remains available for compatible automation and the legacy desktop client; the browser uses the protected web session cookie instead. Steam accounts are added from the web dashboard with their Steam username/password, followed by manual Steam Guard or e-mail confirmation when requested.

The browser dashboard contains Play Ready and Not Play Ready account lists, Prime filters, status refresh, Workshop clearing, server-side Loadout and Personalize presets, account notes and operation confirmations. AutoExec is intentionally not part of the application.

## CI and releases

Every push and pull request builds the backend, Windows bridge and desktop client. Pushing a tag such as `v0.1.0` creates a GitHub Release containing self-contained Windows desktop/bridge archives and a compressed Docker image archive for the backend.

The Steam/CS2 adapter is based on the MIT-licensed `fakearchie/nfatool` reference. See `THIRD_PARTY_NOTICES.md` for attribution.
