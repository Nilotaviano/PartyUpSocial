# Contributing to PartyUp

Thanks for considering a contribution! PartyUp is a spec-first project with an unusual
amount of written-down intent — reading the docs before coding will save you a rewrite.

## Before you write code

1. **Read the relevant part of [PRODUCT_SPEC.md](PRODUCT_SPEC.md).** Product behavior is
   decided there (180 numbered decisions). PRs that contradict a Confirmed decision will
   be declined unless the spec discussion happens first — open an issue proposing the
   decision change instead.
2. **Read [ARCHITECTURE.md](ARCHITECTURE.md).** The dependency rule, time-handling rules
   (NodaTime only in domain code), and the decision record (A-numbers) are binding.
3. **Open an issue for anything non-trivial** before investing significant effort.

## Contributor License Agreement

PartyUp is AGPL-3.0, and the Maintainer retains the ability to relicense the Project
(including commercially). To keep that possible, every contribution needs the
[Contributor License Agreement](CLA.md) — you keep ownership of your work and grant the
Maintainer broad license rights, including relicensing.

To agree, include this sentence in your first pull request:

> I have read and agree to the PartyUp Individual Contributor License Agreement (CLA.md, v1.0).

PRs without CLA agreement can't be merged.

## Development setup

Prerequisites and the full local workflow live in the
[README](README.md#-getting-started) and [ARCHITECTURE.md §6](ARCHITECTURE.md#6-local-development-workflow).

Quality bar (CI enforces all of it):

- `dotnet build PartyUp.sln` — zero warnings (`TreatWarningsAsErrors`)
- `dotnet test PartyUp.sln` — unit + integration (integration needs Docker; use
  `--filter "Category!=Integration"` without it)
- `cd web && npm run lint && npm run build && npm test`

## Conventions that matter here

- **Tests name the decisions they prove.** A domain test for a spec rule carries its
  D-number in the test name (e.g. `Confirmed_session_keeps_confirmation_while_going_waitlist_remains_D074`).
- **No changelog comments.** History belongs in commit messages; comments only explain
  non-obvious current-state constraints.
- **Conventional Commits** (`feat:`, `fix:`, `docs:`, …), imperative subject ≤ 72 chars.
- **User-facing strings go through i18next** (en, pt-BR, es) — no hardcoded UI text.
- **Accessibility is scope, not polish** — interactive UI needs keyboard paths and
  screen-reader semantics (WCAG 2.2 AA target).
