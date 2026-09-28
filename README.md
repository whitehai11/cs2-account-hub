# CS2 Account Hub

Server-first CS2 account dashboard for Ubuntu/Docker with a Windows desktop frontend.

The server owns Steam authentication, Steam Guard code flow, CS2 Game Coordinator status queries, Prime classification, cooldown/VAC sorting, Loadout, Personalize and Workshop actions. The desktop client only renders the dashboard and sends commands. A small Windows bridge is used for the one Windows-specific action: handing a selected server account to the locally installed Steam client.

## Project layout

- `src/Shared`: DTOs and enums shared by all components.
- `src/Backend`: ASP.NET Core API, encrypted token storage and sequential operation worker.
- `src/WindowsBridge`: local Steam-client handoff; it never receives a Steam password.
- `src/Desktop`: WinUI 3 frontend.
- `Dockerfile` and `docker-compose.yml`: Ubuntu server deployment.

Accounts are persisted in `/data`, while refresh tokens are protected with ASP.NET Data Protection keys also stored in `/data/keys`. Keep the Docker volume and API key private. Passwords and Steam Guard codes are accepted only in memory during the authentication request and are never written to the account database or logs.

The classification is deliberately conservative: an account is `PlayReady` only when the GC returned cooldown data, VAC is explicitly clear and no cooldown is active. Unknown status stays `NotPlayReady` until a refresh confirms it.

## Build

The repository needs the .NET 8 SDK for development. The current Codex host only has the .NET runtime installed, so compilation must be done on a machine or CI runner with the SDK, or through Docker.

```powershell
dotnet build .\CS2AccountHub.sln
```

```bash
CS2_HUB_API_KEY='replace-with-a-long-random-value' docker compose up --build
```

The compose file expects the external Docker network `npm_default` used by Nginx Proxy Manager and publishes the backend on host port `18080`. Create the network once if it does not exist:

```bash
docker network create npm_default
```

Nginx Proxy Manager can then proxy to `backend:8080` on the `npm_default` network, or to the host on port `18080`.

Never commit real Steam passwords, Steam Guard codes, refresh tokens, or production encryption keys.

The API key is required by the Docker deployment and must also be configured in the desktop client's `CS2_HUB_API_KEY` environment variable. Set `CS2_HUB_API_URL` on the desktop to the public HTTPS URL of the server.

## CI and releases

Every push and pull request builds the backend, Windows bridge and desktop client. Pushing a tag such as `v0.1.0` creates a GitHub Release containing self-contained Windows desktop/bridge archives and a compressed Docker image archive for the backend.

The Steam/CS2 adapter is based on the MIT-licensed `fakearchie/nfatool` reference. See `THIRD_PARTY_NOTICES.md` for attribution.
