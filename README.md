<div align="center">

# 🎮 PartyUp

**Stop asking *"so… when can everyone play?"* — start playing.**

[**partyup.social**](https://partyup.social)

[![CI](https://github.com/Nilotaviano/PartyUpSocial/actions/workflows/ci.yml/badge.svg)](https://github.com/Nilotaviano/PartyUpSocial/actions/workflows/ci.yml)
[![License: AGPL-3.0](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![React 19](https://img.shields.io/badge/React-19-61DAFB)](https://react.dev/)
[![TypeScript](https://img.shields.io/badge/TypeScript-strict-3178C6)](https://www.typescriptlang.org/)
[![PostgreSQL 17](https://img.shields.io/badge/PostgreSQL-17-4169E1)](https://www.postgresql.org/)

*Pre-launch · built in public · spec-first*

</div>

---

Every friend group has the same problem: five people, five schedules, one game everyone wants to play, and a group chat full of *"I can do Thursday… actually no, Friday?"* that ends with nobody playing anything.

PartyUp gives the whole friend group one place to **publish availability**, **propose sessions**, and **actually commit** — so the plan survives contact with real calendars.

> 🚧 **Status:** in active development. The foundation (walking skeleton, CI/CD, deploy stack) is complete; product features are being built toward a single full-scope public launch. This repo is the real, working codebase — watch it grow.

## ✨ What it does (launch scope)

- 📅 **Fixed-time sessions** — title, game, platform, capacity; invitees RSVP Going / Maybe / Declined
- ✅ **Auto-confirmation** — a session confirms itself the moment enough people are Going, with waitlists, manual promotion, and carefully specified edge cases (capacity edits, start-time changes, mid-session joins)
- 🗳️ **Scheduling polls** — can't pick a time? Offer up to 12 candidate slots, let everyone vote Available / Maybe / Unavailable, finalize the winner into a real session with fair, first-come capacity allocation
- 🕒 **Availability sharing** — one-time or weekly blocks on a drag-and-drop week grid (with full keyboard access), merged and ranked into *"here's when you can all play"* recommendations
- 🔗 **Guest links** — friends without accounts RSVP or vote through a secure personal link; no signup wall
- 👥 **Real friend graph** — friend codes, QR codes, profile links, Steam friend-list import; blocking with mutual invisibility
- 🌍 **Timezone-correct by construction** — NodaTime end to end; weekly availability keeps its wall-clock time across DST transitions
- 🔔 **Web Push + in-app notifications** — invitations, confirmations, reminders, poll deadlines; per-session mute
- 🗣️ **Three languages** — English, Português (BR), Español, including notifications and emails
- ♿ **WCAG 2.2 AA target** — keyboard-first drag alternatives, screen-reader announcements, reduced-motion support

The complete behavior is specified before code in [PRODUCT_SPEC.md](PRODUCT_SPEC.md) — **180 numbered product decisions** (D-001…D-180), each one traceable to the test that proves it.

## 🏗️ How it's built

```mermaid
graph LR
    B((Browser)) -->|HTTPS| C[Caddy<br/>auto-TLS]
    subgraph VM [Single VM · Docker Compose]
        C --> A["ASP.NET Core API<br/>+ React SPA (static)"]
        A --> P[(PostgreSQL 17)]
        K[Nightly pg_dump<br/>30-day retention] --> P
    end
```

One deployable unit, boring on purpose. Behind that simplicity, the codebase is layered strictly:

```
Domain → nothing        pure C# state machines, visibility & overlap engines (NodaTime only)
Application → Domain    use cases, ports, domain events
Infrastructure → both   EF Core + Npgsql, migrations, integrations
Api → all three         composition root: minimal APIs, OpenAPI, health, SPA hosting
```

| Choice | Why |
|---|---|
| **ASP.NET Core (.NET 10) minimal APIs** | First-class OpenAPI; the API document *is* the future mobile contract |
| **PostgreSQL + EF Core + NodaTime** | `timestamptz ↔ Instant` mapping; scheduling apps live or die on time correctness |
| **React 19 + Vite + TypeScript (strict)** | Typed API client generated from the served OpenAPI document — no drift |
| **TanStack Query + Tailwind v4 + Radix** | Server state where it belongs; accessible primitives |
| **Cookie auth (BFF), same-origin** | SPA is served by the API container — no CORS, no token juggling |
| **Hangfire (in-process)** | Status transitions are materialized by an idempotent sweeper, never re-derived per query |
| **Docker Compose on one VM** | Sized honestly for friend-group scale; the load-test phase decides if that ever changes |

Engineering practices that keep it honest:

- 📋 **Spec-first, decisions on record** — product decisions (D-numbers) and architecture decisions ([A-001…A-020](ARCHITECTURE.md#9-decision-record-foundation)) are written down, numbered, and cited in code reviews and test names
- 🧪 **Tests named after the rules they prove** — domain tests carry decision IDs (`grep D_074` finds the waitlist-grace rule); integration tests run against real PostgreSQL via Testcontainers
- 🚦 **CI-gated CD** — a red build can never reach the server; images are immutably sha-tagged for one-command rollback
- 🔐 **Security as design input** — guest links are hashed bearer credentials (256-bit CSPRNG, never logged), visibility checks fail closed, deletion compliance survives database restores
- ⚠️ **Zero-warning builds** — `TreatWarningsAsErrors` + analyzers across every project

## 🚀 Getting started

Prereqs: **.NET SDK 10**, **Node 24+**, **Docker** (optional — local Postgres + integration tests; works fine [inside WSL](ARCHITECTURE.md#6-local-development-workflow)).

```sh
# once
dotnet tool restore
cd web && npm ci && cd ..

# database (or point ConnectionStrings__Db at any Postgres 17)
docker compose -f deploy/compose.dev.yml up -d
dotnet dotnet-ef database update --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api

# run
dotnet run --project src/PartyUp.Api     # API on :5000 (OpenAPI at /openapi/v1.json)
cd web && npm run dev                    # SPA on :5173, proxied to the API
```

Verify your setup:

```sh
dotnet build PartyUp.sln                                   # 0 warnings, or it doesn't ship
dotnet test PartyUp.sln --filter "Category!=Integration"   # unit tests (no Docker needed)
dotnet test PartyUp.sln                                    # + integration tests (real Postgres via Testcontainers)
cd web && npm run lint && npm run build && npm test
```

## 📚 Documentation

| Document | What's inside |
|---|---|
| [PRODUCT_SPEC.md](PRODUCT_SPEC.md) | Full product behavior — 180 numbered, statused decisions |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | Phased delivery plan with honest sizing |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Technical foundation, conventions, decision record |
| [deploy/](deploy/) | Dockerfile, compose files, Caddy config, backup & restore runbooks |

## 🗺️ Roadmap

| Phase | Scope | Status |
|---|---|---|
| 0 | Walking skeleton — CI/CD, deploy stack, health, migrations | ✅ code complete |
| 1 | Domain core — session/poll state machines, visibility & overlap engines, D-number test suite | 🔜 next |
| 2 | Accounts, auth (email, magic link, Google, Steam), onboarding | ⬜ |
| 3 | Friends, blocking, Steam import | ⬜ |
| 4 | Fixed-time sessions end-to-end, guest links | ⬜ |
| 5 | Availability, recommendations, calendar views | ⬜ |
| 6 | Scheduling polls | ⬜ |
| 7 | Notification transports & preferences | ⬜ |
| 8 | Localization, WCAG 2.2 AA audit, hardening | ⬜ |
| 9 | Private beta → public launch | ⬜ |

Everything ships together in one public v1 — no stripped-down beta ([D-163](PRODUCT_SPEC.md#14-decisions)).

## 🤝 Contributing

Issues and PRs welcome — read [CONTRIBUTING.md](CONTRIBUTING.md) first (spec-first workflow, quality bar, and the [CLA](CLA.md) that keeps the project's licensing options open).

## 📄 License

[AGPL-3.0](LICENSE) © 2026 Nilo Silva.

Copyleft on purpose: you're welcome to read, run, learn from, and improve PartyUp — but if you build a service on it, your improvements come back to the commons. Network use counts as distribution. Contributions are accepted under the [CLA](CLA.md).

---

<div align="center">

Built in the open by [**Nilo Silva**](https://github.com/Nilotaviano) · [partyup.social](https://partyup.social)

</div>
