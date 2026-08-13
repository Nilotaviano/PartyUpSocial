# Styling overhaul — UI/UX design pass (IMPLEMENTATION_PLAN § "Visual design pass")

**Owner rulings (2026-08-13):** keep the Tabletop Club visual identity (PHASE5_1_DESIGN moodboard C, ratified 2026-08-07) — palette, type, offset-shadow motif, dark mode all stand. Overhaul the UI/UX on top of it to a professional, polished bar; fresh interaction/layout ideas explicitly welcomed. This document is the binding design for that pass. Review status: **rev 5 — CONVERGED. Rounds 1 (66), 2 (28), 3 (16) triaged and folded in; round-4 verifier verdict CONVERGED with 5 residuals (2 Important, 3 Minor), all folded into this revision.**

## 1. Scope and non-scope

**In scope:** page scaffolding, component-system completion, interaction/UX regrouping of shipped screens, motion utilities, empty states, destructive-action placement, responsive polish, dead-code retirement. Markup/CSS/i18n-key additions only. **No new dependencies.**

**Not in scope:** palette/type token *values*; API/OpenAPI change; **any functional behavior change** — every control renders under today's conditions with today's role and accessible name unless §8 names the rewrite; WeekGrid internals; **VotingGrid's controls** — the three vote buttons, their glyphs, `role=radio` radiogroup, roving tabindex, accessible names, and `data-testid`s are frozen (PHASE6_DESIGN §5 verbatim); *the option-row `<li>` container chrome, its datetime heading, and tally markup are explicitly in scope for restyle* — this carve-out is the only VotingGrid edit permitted; poll **finalization radios**, and **all wrapping-label native controls** (they keep their structure and get skin only, §3.2); QR chip tokens; email templates; the tour's step map and anchor mechanism.

**Floors:** WCAG 2.2 AA, axe zero-exclusion, Phase 8 interaction-state scans (list **grows**, §7); D-139 class-duty loops — never narrowed, extended per §3.2; D-188/D-068 request cards username-only (visible + tree); D-076 masked rows layout-identical; P5-TOUCH-01; i18n parity ×3; reduced-motion; WeekGrid ≥44px; no horizontal scroll ≥360px; **all eight `data-tour` anchors** (six in App.tsx — wordmark + five nav links — plus NotificationBell, AccountMenu; one per `TOUR_STEP_IDS`) stay mounted at every width; D-182 facts stay green unmodified — `Spotlight_ScrollsTheOffScreenAnchorIntoViewAtANarrowViewport_D182` (375×667, Ratio=1) runs in the S1 gate by name; persistent `p[aria-live="polite"]` regions (session/poll detail, ShareCard, SteamImport) stay mounted with their announcement text as the source of truth; PageHeader carries the h1 focus-on-mount contract (`tabIndex={-1}`, `focus:outline-none`, opt-in `aria-live` — `focuses the page h1 on mount_D139`); friend-row identity link keeps accessible-name order; session roster keeps the `Roster` heading and the four per-status subgroup headings with counts verbatim (`roster status is conveyed by text, not color alone_D139` binds "Going (1)").

## 2. Audit findings (evidence: `.foreman/design-audit/*.png`; premises corrected through review rounds 1–3)

- **F1 ShareCard layout broken:** copy buttons overlap link text.
- **F2 No page scaffold:** content floats on `bg-page`; card usage arbitrary; footer floats on short pages.
- **F3 Session detail shows no state:** no status/progress on the flagship screen; actions ungrouped; `Lembrete` label collides; Cancelar sessão mid-content.
- **F4 Destructive actions lead** on friend rows and add-friend (identity link already ships; destructive dominance is the defect).
- **F5 Push prompt renders above the h1** outside the content column (its one-shot gating is correct).
- **F6 Onboarding lacks warmth affordances:** no visual progress, no brand moment, raw fields (finish slap ships; navigation untouched).
- **F7 Control skins inconsistent:** shipped Radix primitives are themed; native controls (checkboxes in seven files, selects/numbers/datetimes) render default UA styling — blue boxes on cardstock. *Fix is skin, not primitive migration* (wrapping-label structures are test-bound and behavior-relevant).
- **F8 Empty states inert.**
- **F9 Poll option rows visually flat:** datetime heading and tally *already ship* inside VotingGrid rows; missing are row prominence (chrome) and the viewer's current vote restated in text.
- **F11 No brand mark; welcome below-fold dead.**
- **F12 Form pages raw:** page-level defaults paragraph; `mm/dd/yyyy` native placeholder in localized UI.
- **F13 Dead code, measured:** `legacy` variant 0 consumers; `.text-slate-500` 0 call sites; ~15 genuine arbitrary values.
- **F14 Transient feedback invisible:** copy actions announce SR-only; no consistent visible confirmation.

## 3. Design system additions

### 3.0 Tokens & utilities

```css
@theme {
  --container-page: 72rem;   /* → max-w-page */
  --container-form: 44rem;   /* → max-w-form */
}
@utility motion-press     { transition-duration: 80ms; }
@utility motion-pop       { transition-duration: 120ms; }
@utility motion-celebrate { transition-duration: 300ms; }
```

Motion as `@utility` (offset-shadow precedent); `--spacing-*` stays the 4px scale; inline `duration-75` → `motion-press` (75→80ms drift correction).

### 3.1 Page scaffold (F2)

- Shells own chrome (`min-h-dvh` flex column, footer pinned); **pages keep their own `<main>`**.
- **`PageScaffold`**: centers `max-w-page`/`max-w-form`, `px-4 sm:px-8`, `gap-8`.
- **`PageHeader`**: h1 + ≤2 header actions + subtitle/meta row; h1 focus contract (§1); actions wrap below h1 on `<sm`.
- **`Section` vs `SectionCard` predicate:** `SectionCard` (ink outline + offset shadow) = a record of a thing existing independently of the page (share-code object, the viewer's own RSVP block) **or a hazard boundary** (danger zone). Everything else — including triage/attention groupings — is a `Section` (heading + `gap-6`, no border). Repeated person/session rows = hairline rows (`border-t border-ink/25`) inside one Section, every screen. A bordered card never contains another bordered card. ≤3 SectionCards per page.
- **`FormShell`**: `max-w-form` SectionCard; per-field helper text; primary right, ghost cancel left. Native datetime keeps; locale echo line + empty-state format hint.
- **`SubNav`**: shipped active-pill styling, `aria-current="page"`, below PageHeader.
- **Screen-state contract:** pending→skeleton; query error→StatusBanner danger (+retry where handler exists); fail-closed 404→existing copy in scaffold; mutation feedback→§3.5.

### 3.2 Controls (F7, F12)

- **Skin-only unification.** All native controls — the seven checkbox files, native radios (finalize, dialogs, guest), selects, numbers, datetimes — **keep their exact DOM structure** (wrapping labels included; their tests bind `closest('label')`, `type="checkbox"` attributes, and label-click toggling) and receive: `.control-frame` recipe (2px ink border, radius-control, bg-card, focus-ring triple) where box-like, `accent-color: var(--color-primary)` on checkbox/radio inputs, custom chevron on native selects. Shipped Radix primitives (`ui/checkbox`, `ui/select`) and hand-rolled radio patterns (ThemeToggle, AvatarPicker) restyle via classes only. **No structural migration, no new packages.**
- **`SegmentStrip`** — visual only, on existing semantics: RSVP keeps `aria-pressed` buttons in `role=group` (never a radiogroup — D-084 arrow-key hazard); filters/theme restyle their shipped semantics. Below `sm`: stacks to full-width 44px rows.
- **Icon-button recipe:** `min-h-11 min-w-11`, centered glyph, `aria-label`, **and the focus-ring triple** (`focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus`) — the exact strings the D-139 loops grep.
- **`ActionMenu`**: Radix DropdownMenu behind an icon button; destructive items `text-danger`, plain words, consequence copy as item description. Dialog composition: controlled dialog + `onSelect preventDefault`; focus returns to menu trigger on close.
- **D-139 duty extensions, placed where hosts exist:** S1 ships `ActionMenu.test.tsx` — a component-level duty fact that mounts the primitive, opens it, and asserts every `menuitem` has `min-h-11` + a `data-[highlighted]:` treatment (Radix items correctly use highlight, not focus-visible). S2 extends the FriendsAccessibility loop with an open-menu `menuitem` pass when the first page-level menu lands. S4 adds a poll-detail class-duty fact covering `getAllByRole('radio')` **scoped to `role=radio` button controls only** (VotingGrid buttons carry `min-h-11` + the focus triple already); the bare native finalize inputs are exempt by name — their 44px duty is satisfied by the ≥44px wrapping-label row plus `accent-color`, and their glyph does not grow. S5 adds the same button-scoped fact for ThemeToggle/AvatarPicker. Resting `button` loops in the two existing hosts are untouched.

### 3.3 Identity & social surfaces (F1, F4)

- **`IdentityRow`** variants: `full` (avatar + display name + *optional* `@username` slot; absent renders nothing — rosters/voters have no username on the wire), `handle` (username only; no displayName/avatar props — leak is a type error; D-068/D-188 gate), `masked` (busy pair, ordinary).
- **Friend rows** (one Section, hairline rows): shipped identity link + trailing **ActionMenu**: Remover amigo (existing dialog, §3.2 composition), Bloquear @name (**one-click unchanged**; consequence as item description). No resting red buttons. Accepted residual: no new positive affordance this pass (profile link already ships; "Convidar" preselection = backlog).
- **Add-friend / profile / Steam import:** Enviar solicitação primary; **Bloquear stays a plain danger-text button** with visible consequence sentence — their tests untouched.
- **ShareCard:** one SectionCard; QR left; code label + mono h2; **three copy buttons keep their visible text labels** (their accessible names — never icon-only); link rows `grid grid-cols-[1fr_auto] gap-2`, CSS-only truncation (full-URL accessible names preserved). Existing `p[aria-live]` announcement stays the source of truth (`ShareCard.test.tsx` binds); visible confirmation is additive via Toast (§3.5).
- **Steam import (S2):** scaffold + IdentityRow across shipped result states.

### 3.4 Session & poll detail (F3, F9)

**Session detail order — regrouping only; all controls render under today's conditions** (join/withdraw pair and unconditional Sair unchanged; backlog note for a viewer-join-status contract delta):

1. `PageHeader` + **status hero**: chip for all five statuses — Unconfirmed "Aguardando jogadores", Confirmed "Confirmada", Completed "Concluída", Expired "Expirada", Canceled "Cancelada" — plus shipped `detail.inProgress` as a supplementary chip; unknown → no chip. **Progress line only while `status === 'unconfirmed'`**: "{{going}} de {{min}} confirmados" — numerator = roster rows with status Going and `!isWaitlisted` (mirrors `IsActiveGoing`, D-062); denominator `minAttendance`; waitlist suffix only when `maxCapacity` non-null. Confirmed shows the chip alone (D-074 grace makes a below-minimum Confirmed legitimate — no contradicting denominator). Chip plays the 300ms slap only on an observed Unconfirmed→Confirmed transition.
2. Meta row: date/time, platform, game chips; notes; "Silenciada" chip when muted (sessions only).
3. **RSVP SectionCard**: SegmentStrip-styled `aria-pressed` buttons (selectors survive) + today's secondaries as ghosts.
4. **"Pedidos de entrada"** organizer-only Section (join-request rows, count-badged).
5. Roster Section: **`Roster` heading and the four per-status subgroup headings with counts survive verbatim** ("Going (1)" — D-139 text-not-color fact); IdentityRow(full, no username) hairline rows beneath them.
6. **Waitlist:** shipped top-level section stays — `Waitlist (n)` heading, all viewers, absent when empty, promote/reject organizer-gated. Restyle only.
7. **Notificações desta sessão** Section (heading key §6): reminder select + mute toggle side by side; D-090/D-113 consequence copy visible in meta type.
8. **Danger zone** SectionCard (heading key §6), last: Cancelar sessão.

**Poll detail:** PageHeader chip reuses the seven shipped `polls:status.*` strings verbatim; **no participation line** (no wire source). Option list: VotingGrid's `<li>` chrome restyled to hairline rows per the §1 carve-out (heading/tally markup restyled in place; controls frozen), **plus** the viewer's current vote restated in text per row (new element inside the carve-out; `data-testid`s and accessible names untouched); the option list is never wrapped in a bordered card. Sugerir horário + guest links Sections; voters Section; **"Notificações desta enquete"** Section = mute toggle + visible `mute.copy`/`mute.disabledTerminal` only (**no reminder row — polls have none**; `aria-pressed`/terminal semantics unchanged; no poll meta row — mute state reads from the toggle; `detail.muted` is sessions-only); danger zone last. Guest session/poll/invalid: same scaffold, shipped states via §3.1 contract, VotingGrid shared → guest voting unaffected.

### 3.5 Feedback taxonomy (F5, F14)

- **Existing live regions are the announcement source of truth everywhere** — no announcement node moves; all bound tests keep passing.
- **Toast = additive visual chrome** for transient confirmations (copy, mark-read, undo). **Mechanism:** S1a ships a shell-owned toast queue (`Toast.Provider` + `Viewport` in the shell; region-label keys in §6) plus a `useToast()` dispatcher that **no-ops when the shell store is absent** — components never render `Toast.Root` themselves, so provider-less component tests see no toast and no throw. Components fire toasts *in addition to* their live-region text. *Implementation residual (S7, accepted): WeekGridPage keeps its local provider permanently — its test file mounts the page without the shell, and three undo-toast facts bind the local provider's labels; consolidation would require test rewrites §8 does not authorize.*
- **StatusBanner** = persistent page-scoped state (form/query errors, "Solicitação aceita", push prompt). Variants info accent / success primary / danger. Where it accompanies a bound `p[aria-live]`, only text enters the live region; banner chrome (border, icon, dismiss ✕) is a sibling wrapper; interactive children never inside the region.
- **Push prompt:** eligibility, persistence, D-175 fallback arm, shell mount all stay. S1 reshapes in place: compact single-row StatusBanner in a centered `max-w-page` gutter-aligned container. S5 lands the PageScaffold portal slot on `/app` + `/notifications` (below PageHeader; fallback = shell position for unmigrated routes) and runs the D-147/D-159/D-175 facts in its gate.

### 3.6 Empty states & brand (F8, F11, F6)

- **`EmptyState`**: illustration + product-voice sentence + CTA. `variant="quiet"` (no CTA) wherever empty is a *good terminal state* (Notifications inbox, blocked list — `empty.blockedHelp` as body); CTA required everywhere else. Instances: Friends→share CTA, requests (existing keys), Sessions→Criar sessão, Polls→Criar enquete, Discover→"Adicione amigos…"→/friends, Home below.
- **Brand mark:** two offset time-blocks overlapping into one filled cell (welcome-hero figure reduced); ink outline, `#E4572E` fill per the 5.1 brand-moment reservation; inline SVG `aria-hidden` beside the wordmark (nav, welcome, onboarding). S1 verifies dark-mode 3:1; accent fallback if recomputation disagrees.
- **Onboarding:** steps in one centered FormShell; 6-dot progress row (`aria-current="step"`; SR text stays); brand mark above; navigation flow + shipped finish slap untouched.
- **Welcome below-fold:** static 3-step "how it works" band.
- **Home (S5):** PageHeader → attention **Section** (page-scoped triage per the predicate; accent left rule for emphasis) → Próximas sessões Section → Enquetes Section → Encontrar horário CTA → Descobrir Section. **Sections with no items are omitted entirely; fewer than two sections with content → single EmptyState** (share block + Criar sessão). Tour targets preserved. *Implementation residual (S5, accepted): `HomePage.test.tsx:98` binds all five section testids under all-empty data and `:278` binds the polls empty line, so sections stay rendered (unbound placeholder lines removed; quiet-page EmptyState rendered inside the attention section) until that fact is deliberately rewritten in a future pass.*

### 3.7 Navigation & responsive

- Desktop nav stands. `<sm`: **chip row stays**, polish only — scrollbar track hidden, **`scroll-snap-type: x proximity`** (never mandatory; no snap-align on NotificationBell/AccountMenu — the D-182 Ratio=1 containment fact must survive `scrollIntoView`), edge-fade masks. Nav dropdown deferred (unmounts 5/8 tour anchors; breaks the 375×667 D-182 overflow fact). Accepted residual: off-screen destinations still need a swipe.

### 3.8 Dead-code retirement (F13)

S1: delete `legacy` variant + default fallback (→`neutral`), `.text-slate-500` bridge, stale comments. S8: ~15 genuine arbitrary values; `data-[…]` variants exempt.

## 4. What deliberately does not change

VotingGrid controls + finalize radios + all wrapping-label natives (structure); WeekGrid; AuditedRoutes inventory; one-click block; Sair/join/withdraw conditions; onboarding navigation; push semantics; tour map + eight anchors + D-182 facts; roster/waitlist heading contracts; guest/legal/deletion content; email templates; existing i18n keys; live regions.

## 5. Route→slice matrix (AuditedRoutes reconciled)

S2: friends, add-friend, profile, steam-import. S3: session detail/list ×2/create/edit/invite, guest-session, guest-invalid. S4: poll detail/list/create/invite, guest-poll. S5: home, notification center, settings ×4 incl. blocked-users (single owner; SubNav), theme picker, push slot. S6: onboarding ×7 (index + six steps), auth, welcome, age-gate, legal ×3, deletion ×2, email-unsubscribe. S7: calendar, agenda, find-a-time, discover, admin ×3 (scaffold + state contract; content-removal = separate destructive row in danger zone; unban never implies restoration). S1a/S1b: chrome everywhere.

## 6. i18n keys (complete contract; three locales per owning slice; parity `namespaces` array unchanged)

| Key | Namespace | Surface |
|---|---|---|
| `actions.more`, `actions.dismiss`, `actions.retry` | common | ActionMenu trigger, StatusBanner ✕, query-error retry |
| `toast.regionLabel`, `toast.dismissLabel` | common | shell Toast viewport (S1) |
| `status.unconfirmed/confirmed/completed/expired/canceled` | sessions | status chip (new block — none exists; `detail.inProgress` reused) |
| `detail.progress`, `detail.progressWaitlist` | sessions | progress line (unconfirmed only) |
| `detail.muted` | sessions | meta chip |
| `detail.joinRequests`, `detail.notificationsHeading`, `detail.dangerZone` | sessions | §3.4 section headings |
| `detail.notificationsHeading`, `detail.dangerZone`, `detail.yourVote` | polls | poll sections + per-row vote restatement |
| `emptyState.sessions.{body,cta}` / `emptyState.polls.{body,cta}` | sessions / polls | list empties |
| `emptyState.discover.{body,cta}`, `emptyState.home.{body,cta,ctaSecondary}` | home | discover + empty Home |
| `welcome.how.{1,2,3}.{title,body}` | auth | welcome band |
| `onboarding.progress` | onboarding | dot-row SR label |
| `form.dateHint`, `form.dateEcho` | common | datetime echo |

Poll status chips reuse `polls:status.*` (7 shipped). Existing keys reused when found under other names; no new namespace files.

## 7. Slices & gates

Per slice: web gate (`lint && build && npm test`); WSL browser lane on touched routes, both themes, + the slice's new interaction-state facts; full wsl-gate + web gate + browser lane before push. Interaction-state facts: **S1a** `status-banner-visible` (push prompt); **S2** `friend-actionmenu-open`. **S1a's** browser gate names `Spotlight_ScrollsTheOffScreenAnchorIntoViewAtANarrowViewport_D182` explicitly.

| # | Content |
|---|---|
| S1a | Tokens/utilities, shell column + footer pinning, PageScaffold/PageHeader/Section/SectionCard, **StatusBanner + live-region rule** (its consumer, the reshaped push prompt, lands here), chip-row polish (snap proximity), shell toast queue + `useToast()`, dead-code deletion — all verifiable on shipped chrome |
| S1b (lands with or immediately before S2) | ActionMenu (+`ActionMenu.test.tsx` duty fact), EmptyState, IdentityRow, SegmentStrip, icon-button recipe, control-frame/accent skins, brand mark |
| S2 | ShareCard, Friends (+FriendsAccessibility menu-pass extension), add-friend, profile, Steam import |
| S3 | **FormShell** (first consumer), session detail (§3.4 order, status hero, no-mutation-traversal + per-status chip + progress-arm tests), session list/forms, guest-session/invalid |
| S4 | Poll detail (option-row carve-out, status chips, notifications section, poll radio duty fact), poll list/forms, guest poll |
| S5 | Home anatomy, notification center, settings ×4 + **SubNav** (first consumer) + theme strip (+radio duty fact), push slot portal (+D-147/D-159/D-175 facts in gate) |
| S6 | Onboarding ×7, auth, welcome band, age-gate, legal, deletion, email-unsubscribe |
| S7 | Calendar/find-a-time scaffold + skins (grid internals untouched), discover, admin ×3 (toast-provider consolidation dropped — accepted residual per §3.5; AgendaView skin moved to S8) |
| S8 | Arbitrary-value sweep, 360px sweep, dark audit, full axe matrix, i18n parity, TRACEABILITY + IMPLEMENTATION_PLAN closure, doc deltas |

## 8. Named test rewrites (exhaustive; anything else failing = slice defect)

- `FriendsAccessibility.test.tsx` focus-restore fact → menu-first flow, focus to menu trigger (S2).
- `FriendsPage.test.tsx:177` block flow + `_D105` unfriend fact → `menuitem` queries behind opened menu (S2). AddFriendPage/ProfilePage/SteamImportPage block tests untouched.
- New duty facts (additions, not rewrites): `ActionMenu.test.tsx` (S1b); FriendsAccessibility open-menu pass (S2); poll-detail radio loop (S4); ThemeToggle/AvatarPicker radio loop (S5); S3's chip/progress/no-mutation tests.
- Checkbox/radio/select skinning changes **no test** (structure untouched).

## 9. Risks

- **R1** Status hero fields: `status`, roster entries, `minAttendance`, `maxCapacity` (nullable) — S3 re-verifies shape first; chip alone is the floor.
- **R2** VotingGrid carve-out discipline: S4's diff to VotingGrid.tsx may touch only the `<li>` container/heading/tally markup + add the vote-restatement line; any control-line diff is a defect. **The `<li>` class list stays unconditional** (no viewerVote/elapsed-dependent classes) and the vote restatement is a text node only — the D-140 fact asserting byte-identical class lists on elapsed vs future options stays green unmodified (`PollDetailPage.test.tsx:122-134`).
- **R3** Push-slot portal keeps the D-175 fallback arm rendering wherever mounted; shell-position fallback is the compatibility guarantee.
- **R4** Brand-mark dark contrast recomputed in S1; accent fallback specified.
- **R5** Toast provider must not alter component-test behavior: toasts are fire-and-forget; absence of provider in a test tree is silent.
