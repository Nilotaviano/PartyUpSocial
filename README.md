# PartyUp

PartyUp ([partyup.social](https://partyup.social)) helps friends find a workable time and organize
multiplayer game sessions — propose a session, collect availability, run a scheduling poll when the
time isn't obvious, and track who's actually in, without the back-and-forth of group chats. Product
behavior: [PRODUCT_SPEC.md](PRODUCT_SPEC.md). Phasing: [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).
Technical foundation this repo implements: [ARCHITECTURE.md](ARCHITECTURE.md).

## Stack

ASP.NET Core (.NET 10) minimal-API backend, a React + TypeScript (Vite) SPA served out of the same
container, PostgreSQL 17 via EF Core/Npgsql/NodaTime, Hangfire for background jobs — shipped as one
Docker image behind Caddy on a single VM. Full breakdown: ARCHITECTURE.md §1.

## Prerequisites

- .NET SDK 10 (pinned by `global.json`; verified with 10.0.301)
- Node 24+ and npm 11
- Docker — optional; only needed for integration tests (Testcontainers) and a local Postgres via
  `deploy/compose.dev.yml`

## Quickstart

Everything below was verified end-to-end from a fully clean checkout (wiped `bin/`, `obj/`,
`node_modules/`).

Local Postgres — needed for a healthy `/health` and for the integration test suite — comes from
`docker compose -f deploy/compose.dev.yml up -d` (default connection string in ARCHITECTURE.md §6).
Without Docker, skip that: `dotnet test --filter Category!=Integration` (used throughout below) runs
clean with no database at all, since the integration project is excluded rather than failing.

### .NET (from the repo root)

```sh
dotnet tool restore                                                  # restores dotnet-ef 10.0.10
dotnet --version                                                     # 10.0.301, resolved via global.json (10.0.100 / latestFeature)
dotnet restore PartyUp.sln
dotnet build PartyUp.sln                                             # Build succeeded. 0 Warning(s) 0 Error(s)
dotnet build PartyUp.sln --no-restore -warnaserror                   # CI-exact form; 0 Warning(s) 0 Error(s)
dotnet test PartyUp.sln --filter "Category!=Integration"             # exit 0 (no tests yet; integration test correctly excluded)
dotnet test PartyUp.sln --no-build --filter "Category!=Integration"  # exit 0
dotnet publish src/PartyUp.Api -c Release -o <outdir>                # emits PartyUp.Api.dll (Dockerfile ENTRYPOINT target)

# Migrations are discoverable with zero database contact (never run `database update` locally):
dotnet dotnet-ef migrations list --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api --no-connect
```

### Web (from `web/`)

```sh
npm ci                   # exit 0 (reports 4 high-sev advisories in a devDependency; does not fail)
npm run lint             # eslint .
npm run build            # tsc -b && vite build -> dist/
npm test                 # vitest run -> 1 passed (1)
npx prettier --check .   # All matched files use Prettier code style!
npm run gen:api          # requires the API running on :5000; regenerates src/api/schema.d.ts
```

### Boot smoke — dev

```sh
dotnet run --project src/PartyUp.Api   # http://localhost:5000 (launchSettings), matches the Vite proxy target
#   GET /openapi/v1.json -> 200 application/json
#   GET /health          -> 503 Unhealthy (no local Postgres; process stays up)
#   GET /api/v1/nope     -> 404
#   GET /                -> 404 (no wwwroot in dev)
```

### Boot smoke — production layout

Simulates the Dockerfile's final stage without Docker: copy `web/dist` to `<publishdir>/wwwroot`,
then from `<publishdir>`:

```sh
ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:8080 dotnet PartyUp.Api.dll
#   GET /                    -> 200 text/html   (SPA index)
#   GET /sessions/abc        -> 200 text/html   (client-side routing fallback)
#   GET /assets/index-*.js   -> 200 text/javascript
#   GET /api/v1/nope         -> 404, empty body (was 200 text/html before the fix)
#   GET /health              -> 503 (no Postgres; process stays up)
```

### Hygiene

```sh
git status --porcelain -uall   # no bin/ obj/ node_modules/ dist/ TestResults/ entries after full builds + npm ci
```

## Repo layout

```
PartyUp.sln
global.json                    — pins the .NET SDK major version
/src
  PartyUp.Domain                — entities, state machines, visibility/overlap engines (pure C#, NodaTime only)
  PartyUp.Application            — use cases, ports, domain-event contracts, authorization rules
  PartyUp.Infrastructure         — EF Core (AppDbContext, migrations), Npgsql; later Identity, Hangfire, Web Push, email
  PartyUp.Api                    — composition root: minimal API endpoints, OpenAPI, health, static SPA hosting
/web                             — React + TypeScript SPA
/deploy                          — Dockerfile, compose files, Caddyfile, backup + restore runbooks
/tests
  PartyUp.Domain.Tests           — pure unit tests
  PartyUp.Application.Tests      — use-case tests with fakes
  PartyUp.Api.IntegrationTests   — WebApplicationFactory + Testcontainers (real Postgres)
/.github/workflows               — ci.yml, cd.yml
```

Dependency rule (enforced by project references): `Domain → (nothing)` · `Application → Domain` ·
`Infrastructure → Application, Domain` · `Api → Infrastructure, Application, Domain`. Full rationale:
ARCHITECTURE.md §2.

## Deploy

`cd.yml` runs only after CI succeeds on main: it builds one Docker image (API + built SPA) from the
commit CI validated, pushes it to GHCR, then SSHes into the VM to run `docker compose pull && up` —
but every job is gated behind the repo variable `DEPLOY_ENABLED=true` plus the `VM_HOST`, `VM_USER`,
and `VM_SSH_KEY` secrets, so it no-ops until all four are set. Compose files, the Caddyfile, and the
backup/restore runbooks live in `deploy/` — start with `deploy/RUNBOOK-backup.md` and
`deploy/RUNBOOK-restore.md`. On the VM, `/opt/partyup` holds `deploy/compose.yaml` under that exact
name (bare `docker compose` only auto-discovers `compose.yaml`), plus `.env`, `Caddyfile`, and
`backup-entrypoint.sh`.

## Status

Phase 0 (foundation) is scaffolded: solution/project skeleton, CI (`ci.yml`), gated CD (`cd.yml`), the
health endpoint, an initial EF Core migration, and dev/prod Docker Compose. No product features yet —
see [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) for the phase breakdown.
