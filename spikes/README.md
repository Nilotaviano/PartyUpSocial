# Phase 0 risk spikes

Throwaway prototypes de-risking the plan's highest-risk unknowns (IMPLEMENTATION_PLAN.md Phase 0). Not part of `PartyUp.sln`, not built by CI, no production polish. Each spike's outcome is recorded as a decision in ARCHITECTURE.md §9 — the code stays only as evidence and reference.

| Spike | Question it answers | Feeds | Dir |
|---|---|---|---|
| A | FullCalendar or custom grid for accessible drag availability entry? | Phase 5 | `spike-a-calendar/` |
| B | Is Steam OpenID login + friends/catalog API viable, incl. private profiles? | Phases 2–3 | `spike-b-steam/` |
| C | Does VAPID Web Push work end-to-end (subscribe, deliver, D-159 flow)? | Phase 7 | `spike-c-webpush/` |

Pass criteria live in each spike's README; raw friction notes in `NOTES.md` where present. `Directory.Build.props` / `Directory.Packages.props` here are intentional empty shields — spikes must not inherit the repo's TreatWarningsAsErrors or Central Package Management.
