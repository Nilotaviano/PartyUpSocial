# Friend Groups — design

Third visibility level for sessions and polls. Approved 2026-08-16 after adversarial review (33 findings folded in). Decision texts below are **proposed** D-198..D-210 plus amendments to existing Confirmed decisions; the PRODUCT_SPEC edit lands with or before the code that implements it, never after.

## 1. Owner rulings (fixed inputs)

- Purpose: privacy + invite convenience.
- Groups are **shared entities**: members see the group's name and roster.
- Membership is **invite/accept** consented; only the owner manages (rename, delete, invite, revoke, remove).
- Group visibility is **strict**: no friend-of-extender inheritance on Groups targets.
- A session/poll targets **1..n groups**; audience = union.
- Group members **see + request-to-join** (D-007 flow); selecting a group is not a mass invite.
- **Any Accepted member** (or owner) may target the group on their own sessions/polls.
- Membership **survives unfriending**; friendship is required only at invite time.

## 2. Domain model

### FriendGroup aggregate (new Domain module `FriendGroups`)

- `Id` Guid v7 (A-015: IDs are not security tokens), `OwnerUserId`, `Name` 1..50 trimmed, unique per owner case-insensitive, `CreatedAt` Instant (NodaTime).
- Owner-only operations; delete is a hard delete cascading membership rows and audience junction rows. Sessions/polls that targeted it live on with a shrunken audience (see §5 zero-group state).
- `FriendGroupMember` child entity, DB-unique `(group_id, user_id)`. States: `Invited → Accepted`; decline, revoke, leave, remove = row deletion. No tombstones — same row model as Friendship (DOMAIN_MODEL §4), not D-105 (which is a push policy).
- Invariants: invite requires an Accepted friendship owner↔invitee **at invite time**, validated without cross-aggregate locking — a racing unfriend yields a state the model independently permits (§12 note). No self-invite. Owner is an implicit member with no row.
- Caps, app-checked: 50 owned groups per user (create locks the owner's `users` row `FOR UPDATE` before counting), 100 membership rows per group (Invited + Accepted; owner uncounted; invite/accept lock the group row `FOR UPDATE` before counting — the session-RSVP idiom, ARCHITECTURE §3).

### Group read visibility

Owner + members only; non-members get 404, fail-closed, indistinguishable from nonexistent (the sessions doctrine). `Invited` viewers see name + owner only (enough to answer the invite); `Accepted` see the full roster. Roster identities carry the standard blocked-pair masking (Anonymous User, render-time, D-076 posture).

### Blocking (D-202 proposed)

- Owner↔member block (either direction): the §4 block-effects executor gains a step — hard-delete the membership row, **Invited and Accepted**, silently, in the same transaction as the friendship deletion. Deleting Invited rows is mandatory: otherwise the blocked party could Accept a pre-block invite past the invite-time check. This mirrors D-077 (organizer↔participant block removes the participant); the group owner is the organizer-analog.
- Member↔member block (neither owns): both rows persist; coexistence is masked at render time (D-076 extension — its own decision because D-076 today covers only sessions/polls).
- The group-invite command returns the **identical error** for "not friends" and "blocked" — blocking already deletes the friendship (D-065), and a distinguishable 403 would be a block-detection oracle.

### Unfriending (owner ruling + D-210 proposed)

Membership survives. To keep unfriend meaningful as distancing: the unfriend flow shows, to the actor only, "X remains in your groups: …" with an optional one-tap remove. Backend behavior is unchanged; this is actor-side presentation.

## 3. Visibility engine (security boundary)

- Session and Poll replace `IsPrivate: bool` with `Visibility` enum `Public | Private | Groups` (DB `visibility` smallint: Public=0, Private=1, Groups=2; migration maps false→0, true→1).
- Audience junctions: `session_audience_groups`, `poll_audience_groups`.
- DOMAIN_MODEL §7 rule set becomes (canonical order **4 → 1 → level gate → inheritance**):
  - **Rule 4** (blocking) unchanged, applied first.
  - **Rule 1** (direct: organizer, retained participation, pending join request) unchanged, all levels.
  - **Rule 2** (friend-of-extender inheritance) applies **only when `Public`**.
  - **Rule 2g** (new): when `Groups`, grant iff the viewer is the owner or an Accepted member of ≥1 targeted group. `Invited` grants nothing.
  - **Rule 3** (`Private` = rule 1 only) unchanged.
- New `VisibilityGrant.Group` enum value (append-only per VisibilityGrant.cs directive), result carries the granting **GroupId** — `Inherited(extenderId)` is not reused because a group grant has no extender user.
- `VisibilityTarget` stays viewer-independent (built by `Describe(session/poll)`); the viewer-side resolve input carries the actor's Accepted-membership ids intersected with the target's audience. Actor gates (A-055) **change**: membership rows are loaded after the step-4 parent lock, never precomputed at probe time (a concurrent audience edit makes a probe-time intersection stale at re-resolve).
- SQL predicates (`SessionVisibilitySql`, `PollVisibilitySql`): the rule-2 privacy gate `NOT is_private` becomes `visibility = 0` **exactly** (3 sites; `<> 1` would leak Groups targets to friends-of-extenders — regression-tested); new `EXISTS` branch joining audience × Accepted memberships, plus the owner-implicit `OR`. Fix the stale "No S2 caller" comment at SessionVisibilitySql.cs:21 while editing.

## 4. Lifecycle rules

- **Creation** (D-099/D-125 amendment): a `Groups` session/poll requires ≥1 targeted group at creation; direct invitees optional on top. A `Public` one keeps the ≥1-directly-invited-registered-friend rule.
- **Targeting authority** (D-201): at creation and when **adding** a group to the audience, the organizer must be its owner or an Accepted member. Already-targeted groups are never re-validated on later edits (an organizer who left group G can still edit other fields, or add H, without being forced to drop G).
- **Edit matrix** (D-203): DOMAIN_MODEL §5.6's "becomes private" rule generalizes to **"audience narrows"** — Public→Private, Public→Groups, Groups→Private, or removing a targeted group all retain the current rule-1 set (D-043/D-047 posture) and are silent (D-123). Any edit that leaves the `Groups` level **deletes** the audience junction rows; flipping back means re-picking (no silent resurrection of stale, possibly no-longer-authorized targeting).
- **Poll finalization** (D-204): the visibility level rides the existing `Details` carry (as `IsPrivate` does today); the audience junction rows are **copied** poll→session inside the finalization transaction.
- **Zero-group state** (D-205): group deletion can leave a `Groups` target with an empty audience. Legal degenerate state: engine and SQL degrade to rule-1-only (effectively private), fail-closed. The ≥1-group invariant is creation/edit-time only (the §12-7 D-099 precedent). Affected organizers get an in-app notification that a targeted group was deleted.
- **Member exit** (leave/remove/group-delete): the member loses rule-2g visibility; anything retained by rule 1 (already-RSVP'd participation, pending join request) is kept — falls out of the existing engine. A removed member's outbound targeting (their session aimed at the group) is likewise retained — ratified in a §12 note, consistent with the D-043 retention posture.
- **Account deletion / ban** (D-209, §11 executor): hard-delete every owned group (full cascade) and every membership row (Invited and Accepted) the user holds in others' groups. D-180 admin removal reuses these effects.

## 5. Presentation

- **"via <group>" label** (D-206): session/poll cards show the granting group's name **only** to viewers who are that group's owner or a member, rendered from the live group row (rename-safe). Direct invitees, guest-link holders, pending requesters, and members of only-other targeted groups see a neutral "shared with a group". Presentation on top of real access, never a second visibility channel (the D-196/DOMAIN_MODEL:192 posture). Group names are never serialized to non-members.
- **Notification refs**: an invite notification referencing a since-deleted or revoked group renders masked (dangling-ref rule parallel to tombstones), never 404s the list.

## 6. Notifications (D-207, A-016 transactional)

In-app rows committed in the state-change transaction: group invite received, invite accepted (→ owner), targeted-group deleted (→ affected organizers). Silent: decline, revoke, remove, leave, block-driven removal (D-105 push posture; removal surfaces would be punitive).

## 7. API + persistence

- `/api/v1/friend-groups`: CRUD + member subresource (invite, revoke, accept, decline, leave, remove); list my groups (owned + member-of) and pending invites. All reads/writes through the visibility gate, 404 fail-closed. OpenAPI stays mobile-clean (no web-only conveniences).
- Session/poll create + edit DTOs: `visibility` enum + `audienceGroupIds[]`.
- Tables: `friend_groups`, `friend_group_members`, `session_audience_groups`, `poll_audience_groups`; one migration converts `is_private` → `visibility`.

## 8. Web UI

- Groups page: create/rename/delete, roster (masked where blocked), invite from friends list, pending-invites badge with accept/decline.
- Session/poll create + edit: privacy segment grows to three options; choosing Groups reveals multi-select group chips.
- Unfriend dialog gains the D-210 remains-in-groups note.
- i18n en/pt-BR/es from the first commit; keyboard paths + SR semantics (WCAG 2.2 AA is scope).

## 9. Spec edits (land with or before code)

- Reword from the two-level "non-private" model: D-007, D-018, D-085, D-099, D-118, D-125, D-129 and prose mirrors at PRODUCT_SPEC.md :59, :119, :156, :168, :185, :225, :250 — "non-private" → "public" wherever rule-2 inheritance or the invited-friend requirement is meant.
- New decisions D-198..D-210 as proposed above (Groups level + rule 2g; aggregate; consent; targeting; blocking; edit matrix; finalization; zero-group; presentation; notifications; caps; deletion; unfriend surfacing).
- DOMAIN_MODEL: §7 rule 2g + canonical rule order, §5.6 narrowing generalization, §4 block-executor step, §11 deletion steps, §12 notes (invite-time check is best-effort under races; retained outbound targeting; zero-group degenerate state).
- TRACEABILITY rows for every new/amended decision; tests carry D-numbers.

## 10. Testing

- Engine unit tests per rule interaction (2g × blocking × Invited-vs-Accepted × owner-implicit).
- SQL predicate integration tests, including the `visibility = 0` leak regression (Groups target must not reach friends-of-extenders) and 404-uniformity probes (non-member group reads, Groups-session non-member reads — identical body to nonexistent).
- Gate concurrency: audience edit racing re-resolve; cap races (group create at 49, invite at 99).
- Block executor severing (owner↔member, both directions, Invited and Accepted); pre-block invite cannot be accepted.
- Finalization audience copy; zero-group degradation; junction deletion on leaving Groups.
