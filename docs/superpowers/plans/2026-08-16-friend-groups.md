# Friend Groups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Friend Groups as a third visibility level (`Public | Private | Groups`) for sessions and polls, with a consented shared-group aggregate, per the ratified design.

**Architecture:** New `FriendGroups` Domain module + `ContentVisibility` enum replacing the `IsPrivate` boolean end-to-end (aggregate → engine → SQL predicates → gates → API → web). Rule 2g grants access via Accepted membership in a targeted group; blocking severs owner↔member membership; audience junctions are owned collections on Session/Poll.

**Tech Stack:** .NET 10 / EF Core / Npgsql+NodaTime / xUnit / Playwright / React + TanStack Query + i18next (en, pt-BR, es).

**Spec:** `docs/superpowers/specs/2026-08-16-friend-groups-design.md` (ratified). Read it before any task — every rule below argues from it.

## Global Constraints

- 0 warnings (`TreatWarningsAsErrors`); NodaTime only in Domain/Application; `IClock` injected.
- Visibility fails closed: no access → the same 404/`*_not_found` code as nonexistent, everywhere.
- DB enum values append-only: `ContentVisibility { Public = 0, Private = 1, Groups = 2 }`; `VisibilityGrant` append-only (add `Group = 3`, never renumber).
- Package versions only in `Directory.Packages.props`. No new packages needed.
- All user-facing strings through i18next, all three locales, in the same commit as the UI.
- Tests proving a spec rule carry its D-number suffix (`..._D198`). Update TRACEABILITY.md in the same commit as each D-numbered test batch.
- Conventional Commits, subject ≤72 chars, no changelog comments in code, no session links.
- Full WSL gate (`scripts/wsl-gate.sh`) before any push; `npm run gen:api` needs the API running.
- SQL exactness: the rule-2 gate is `visibility = 0` (Public **exactly**) — `<> 1` leaks Groups targets to friends-of-extenders.

---

### Task 1: Spec amendments (docs only)

**Files:**
- Modify: `PRODUCT_SPEC.md`, `DOMAIN_MODEL.md`, `TRACEABILITY.md`

**Interfaces:**
- Produces: ratified D-198..D-210 text and reworded D-007/D-018/D-085/D-099/D-118/D-125/D-129 that every later task cites.

- [ ] **Step 1: Reword existing decisions.** In `PRODUCT_SPEC.md`, change the two-level "non-private"/"Unless a session is private" phrasings to "public" wherever rule-2 inheritance or the invited-friend creation rule is meant: D-007 (:501), D-018 (:512), D-085 (:577), D-099 (:591), D-118 (:610), D-125 (:617), D-129 (:621), and the prose mirrors at :59, :119, :156, :168, :185, :225, :250. Example — D-099 becomes: "A **public** session requires at least one directly invited registered friend when created; a **Groups** session requires at least one targeted friend group at creation (direct invitees optional). D-198's visibility levels define the terms." Each edit keeps the row's Confirmed status and appends "(amended for D-198)".
- [ ] **Step 2: Append D-198..D-210** to the §14 decision table, transcribing the spec doc's §1–§6 rules one decision each: D-198 Groups level + rule 2g (strict, Invited grants nothing, owner implicit member); D-199 FriendGroup aggregate (owner-only manage, name 1..50 unique-per-owner case-insensitive, hard delete cascades); D-200 membership consent (invite/accept, friendship at invite time best-effort, survives unfriending, row-deletion no tombstones); D-201 targeting (any owner/Accepted member, 1..n groups union, ≥1 at creation, authority re-checked only for newly added groups); D-202 blocking (owner↔member block deletes membership Invited+Accepted silently in the block transaction; member↔member masks; invite endpoint returns the identical not-a-friend error for blocked); D-203 edit matrix ("audience narrows" generalization of D-047, junctions deleted on leaving Groups level); D-204 finalization carries visibility + copies audience junctions; D-205 zero-group degenerate state legal (rule-1-only) + organizer in-app notification on targeted-group deletion; D-206 "via <group>" shown only to that group's owner/members from the live row, neutral "shared with a group" otherwise, names never serialized to non-members; D-207 notifications (invite received, invite accepted, targeted-group deleted; decline/revoke/remove/leave/block silent); D-208 caps (50 owned/user via owner-row lock, 100 membership rows/group via group-row lock, owner uncounted); D-209 account deletion/ban deletes owned groups + own memberships (D-092/D-180 executors); D-210 unfriend flow surfaces remaining memberships with optional remove (actor-side only). Status: Confirmed.
- [ ] **Step 3: DOMAIN_MODEL.md.** §7: insert rule 2g between the privacy gate and rule 2, and state the canonical order verbatim: "4 → 1 → level gate → inheritance (2g for Groups, 2 for Public)". §5.6: generalize the privacy-edit row to "audience narrows" (retained rule-1 set per D-043/D-047; silent per D-123; leaving Groups deletes junction rows). §4 block effects: add the owner↔member membership-deletion step. §11: add owned-group hard delete + own-membership delete to step 4. §12 notes: invite-time friendship check is best-effort under races; removed member's outbound targeting retained; zero-group degenerate state; member↔member roster masking extends D-076.
- [x] **Step 4: TRACEABILITY.md** — rows D-198..D-210 landed as Class `Domain gap`, Phase 10, Verification "planned" (flip each to `Domain` when its D-numbered test lands in Tasks 2+).
- [ ] **Step 5: Commit** `docs(spec): ratify friend groups decisions D-198..D-210`.

---

### Task 2: `ContentVisibility` enum through Domain (Session/Poll/engine)

**Files:**
- Create: `src/PartyUp.Domain/Kernel/ContentVisibility.cs`
- Modify: `src/PartyUp.Domain/Visibility/{VisibilityGrant.cs,VisibilityResult.cs,VisibilityTarget.cs,Viewer.cs,VisibilityEngine.cs,SessionVisibility.cs,PollVisibility.cs}`, `src/PartyUp.Domain/Sessions/{Session.cs,SessionDetails.cs}`, `src/PartyUp.Domain/Polls/{Poll.cs,PollDetails.cs}`
- Test: `tests/PartyUp.Domain.Tests/Visibility/GroupVisibilityTests.cs` (new), existing visibility test files (compile fixes only)

**Interfaces:**
- Produces: `ContentVisibility { Public = 0, Private = 1, Groups = 2 }` (Kernel; persisted, append-only). `FriendGroupId` typed id (`src/PartyUp.Domain/FriendGroups/FriendGroupId.cs`, same pattern as `FriendshipId`). `VisibilityGrant.Group = 3`. `VisibilityResult.Group(FriendGroupId groupId)` with new `FriendGroupId? GroupId` member. `VisibilityTarget.Create(UserId organizerId, ContentVisibility visibility, IEnumerable<FriendGroupId> audienceGroupIds, IEnumerable<VisibilityMember> members, IEnumerable<UserId> pendingRequesterIds)`. `Viewer.Create(UserId id, IEnumerable<UserId>? acceptedFriendIds = null, IEnumerable<UserId>? blockedCounterpartIds = null, IEnumerable<FriendGroupId>? groupIds = null)` where `groupIds` = groups the viewer **owns or holds Accepted membership in**; `Viewer.IsInGroup(FriendGroupId)`. `Session.Visibility`, `Session.AudienceGroupIds` (`IReadOnlyCollection<FriendGroupId>`), `Session.ChangeVisibility(ContentVisibility, IReadOnlyCollection<FriendGroupId> audienceGroupIds, IClock)`; `SessionDetails`/`PollDetails` take `ContentVisibility visibility` in place of `bool isPrivate` (keep parameter position); Poll mirrors; `Poll.Finalize` copies `AudienceGroupIds` onto the minted session.
- Consumes: nothing new.

- [ ] **Step 1: Write failing engine tests** in `GroupVisibilityTests.cs` (follow `SessionVisibilityTests.cs` builder style — `TestSessions`, `TestViewers`, `FakeClock`). Cover, each as its own `[Fact]`:

```csharp
[Fact]
public void Resolve_GrantsAnAcceptedMemberOfATargetedGroupGroupAccess_D198()
{
    FakeClock clock = TestSessions.Clock();
    FriendGroupId groupId = FriendGroupId.New();
    SessionUnderTest s = TestSessions.New(clock, visibility: ContentVisibility.Groups, audienceGroups: [groupId]);
    Viewer member = TestViewers.Of(UserId.New(), groupIds: [groupId]);
    VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, member);
    Assert.Equal(VisibilityGrant.Group, result.Grant);
    Assert.Equal(groupId, result.GroupId);
}
```

Also: friend-of-extender on a Groups session gets `None` (leak regression, `_D198`); `Invited`-only viewer gets `None` — model as viewer WITHOUT the group id, since Viewer carries Accepted/owner ids only (`_D198`); blocked-with-organizer member gets `None` — rule 4 first (`_D202`); Groups session with empty audience list = rule-1-only (`_D205`); Private and Public behavior unchanged (assert one existing case each still passes with the enum, `_D018`/`_D043`); pending join-requester on a Groups session keeps `Direct` (`_D198`); organizer of the session always `Direct` regardless of level (`_D019`). Extend `TestSessions.New`/`TestViewers.Of` with the optional parameters shown.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~GroupVisibilityTests"` — expect compile failure (types missing).
- [ ] **Step 3: Implement.** `ContentVisibility.cs`: enum with doc comment "Persisted; append only, never renumber." `FriendGroupId.cs`: copy the `FriendshipId` typed-id pattern. `VisibilityGrant.cs`: append `Group = 3`. `VisibilityResult.cs`: private ctor becomes `(VisibilityGrant grant, UserId? extenderId, FriendGroupId? groupId)`; keep `Direct`/`NotFound`/`Inherited(extenderId)` factories passing `null` groupId; add `public static VisibilityResult Group(FriendGroupId groupId) => new(VisibilityGrant.Group, null, groupId);`. `VisibilityTarget`: replace `bool IsPrivate` with `ContentVisibility Visibility` + `IReadOnlyCollection<FriendGroupId> AudienceGroupIds`; update `Create`. `Viewer`: add `_groupIds` HashSet + `GroupIds` + `IsInGroup`; extend `Create` (the `From(id, friendships, blocks)` overload stays and passes empty groups — its gate callers get group ids in Task 7). `VisibilityEngine.Resolve` — replace the rule-3 block with:

```csharp
// §7 level gate: Private is rule 1 only (D-043/D-047); Groups admits only
// owners/Accepted members of a targeted group (rule 2g, D-198) — rule-2
// inheritance is Public-only.
if (target.Visibility == ContentVisibility.Private)
    return VisibilityResult.NotFound;

if (target.Visibility == ContentVisibility.Groups)
{
    foreach (FriendGroupId groupId in target.AudienceGroupIds)
    {
        if (viewer.IsInGroup(groupId))
            return VisibilityResult.Group(groupId);
    }

    return VisibilityResult.NotFound;
}
```

Rule-2 code below stays untouched (now reachable only when `Public`). `SessionVisibility.Describe`/`PollVisibility.Describe`: pass `session.Visibility, session.AudienceGroupIds`. `Session.cs`: replace `IsPrivate` prop with `Visibility` + `private readonly List<FriendGroupId> _audienceGroupIds = []` exposed as `AudienceGroupIds`; ctor copies from details + a new ctor/factory parameter `IReadOnlyCollection<FriendGroupId> audienceGroupIds`; the D-099 guard at `Create` (:145) becomes:

```csharp
switch (details.Visibility)
{
    case ContentVisibility.Public when invitees.Count == 0:
        throw new DomainException(DomainError.ValidationFailed,
            "A public session requires at least one directly invited registered friend at creation (D-099).");
    case ContentVisibility.Groups when audienceGroupIds.Count == 0:
        throw new DomainException(DomainError.ValidationFailed,
            "A Groups session requires at least one targeted friend group at creation (D-201).");
}
```

`ChangePrivacy` → `ChangeVisibility(ContentVisibility visibility, IReadOnlyCollection<FriendGroupId> audienceGroupIds, IClock clock)`: `RequireEditable`; if `visibility != Groups` the list must be empty (ValidationFailed) and `_audienceGroupIds.Clear()` (D-203 junction deletion); if `Groups` require ≥1 and replace the list. Authority for newly added groups is the Application layer's job (Task 7). `FromPollFinalization` gains `IReadOnlyCollection<FriendGroupId> audienceGroupIds`. `SessionDetails`/`PollDetails`: `bool isPrivate` → `ContentVisibility visibility`; `PollDetails.ForStartTime` carries it. `Poll.cs`: mirror Session (props, guard D-125-analog, ctor); `Finalize` (:604) passes `[.. _audienceGroupIds]` into `Session.FromPollFinalization` (D-204). Fix every Domain compile error by mechanical `IsPrivate` → `Visibility` mapping (`false→Public`, `true→Private`); Application/Api/Infrastructure will not compile until Tasks 5–8 — that is expected; keep this commit Domain+Domain.Tests-only by building just those projects.
- [ ] **Step 4: Run** `dotnet build src/PartyUp.Domain && dotnet test tests/PartyUp.Domain.Tests --filter "FullyQualifiedName~Visibility"` — all green, including the pre-existing visibility suites.
- [ ] **Step 5: Commit** `feat(domain): replace IsPrivate with ContentVisibility and add rule 2g`.

*(Note: solution-wide build stays red between Tasks 2 and 8 on the enum swap. Do Tasks 2–8 on one branch, gate at Task 9; commit per task regardless.)*

---

### Task 3: FriendGroup aggregate

**Files:**
- Create: `src/PartyUp.Domain/FriendGroups/{FriendGroup.cs,FriendGroupMember.cs,FriendGroupMemberStatus.cs,GroupName.cs,FriendGroupEvents.cs}`
- Test: `tests/PartyUp.Domain.Tests/FriendGroups/FriendGroupTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed class FriendGroup : AggregateRoot
{
    public FriendGroupId Id { get; }
    public UserId OwnerId { get; }
    public GroupName Name { get; private set; }
    public Instant CreatedAt { get; }
    public IReadOnlyList<FriendGroupMember> Members { get; }   // excludes owner (implicit, D-198)
    public const int MaxMembers = 100;                          // rows, owner uncounted (D-208)

    public static FriendGroup Create(UserId ownerId, GroupName name, IClock clock);
    public void Rename(UserId actor, GroupName name);                       // actor != OwnerId → Forbidden
    public void Invite(UserId actor, UserId invitee, bool isAcceptedFriendOfOwner, IClock clock);
    public void AcceptInvite(UserId invitee, IClock clock);
    public void DeclineInvite(UserId invitee);
    public void RevokeInvite(UserId actor, UserId invitee);
    public void RemoveMember(UserId actor, UserId member);
    public void Leave(UserId member);
    public void RemoveCounterpartOf(UserId blockerOrOwner, UserId other);   // block-effects hook, D-202
    public bool HasAcceptedMember(UserId userId);   // true for OwnerId too
    public bool Involves(UserId userId);            // owner, Accepted, or Invited
}
public sealed class FriendGroupMember   // child entity
{
    public UserId UserId { get; }
    public FriendGroupMemberStatus Status { get; private set; }
    public Instant InvitedAt { get; }
    public Instant? AcceptedAt { get; private set; }
}
public enum FriendGroupMemberStatus { Invited = 0, Accepted = 1 }   // persisted, append-only
public sealed record GroupName   // 1..50 trimmed, D-199; GroupName.Create(string) throws ValidationFailed
```

Events (`FriendGroupEvents.cs`, `IDomainEvent` records like `FriendRequestReceived`): `FriendGroupInviteSent(FriendGroupId, UserId OwnerId, UserId InviteeId)`, `FriendGroupInviteAccepted(FriendGroupId, UserId OwnerId, UserId InviteeId)`.
- Consumes: `FriendGroupId` (Task 2), `AggregateRoot`, `DomainException`/`DomainError`, `IClock`.

- [ ] **Step 1: Write failing tests** — one `[Fact]` per rule, `FriendGroupTests.cs`:

```csharp
[Fact]
public void Invite_RequiresAnAcceptedFriendshipAtInviteTime_D200()
{
    FakeClock clock = new(Instant.FromUtc(2026, 8, 16, 12, 0));
    FriendGroup group = FriendGroup.Create(Owner, GroupName.Create("D&D crew"), clock);
    DomainException ex = Assert.Throws<DomainException>(
        () => group.Invite(Owner, Stranger, isAcceptedFriendOfOwner: false, clock));
    Assert.Equal(DomainError.NotFound, ex.Error); // identical to unknown user — no block oracle (D-202)
}
```

Full matrix: create sets owner/name/no rows (`_D199`); rename/invite/revoke/remove by non-owner → Forbidden (`_D199`); invite self → ValidationFailed; duplicate invite → Duplicate; invite existing Accepted → Duplicate; 100-row cap → LimitExceeded (`_D208`); Accept flips Invited→Accepted + raises event (`_D200`); Accept by non-invited → NotFound; Decline/Revoke/Leave/Remove delete the row (assert `Members` no longer contains, `_D200`); Leave by owner → InvalidTransition (owner is implicit, cannot leave own group); `RemoveCounterpartOf` deletes Invited AND Accepted rows for the counterpart (`_D202`); `HasAcceptedMember(owner)` true with no row (`_D198`); GroupName: empty/51-char → ValidationFailed, trims (`_D199`).
- [ ] **Step 2: Run** — expect FAIL (types missing).
- [ ] **Step 3: Implement** the aggregate. `Invite` guard order: actor-is-owner (Forbidden) → invitee==owner (ValidationFailed) → `!isAcceptedFriendOfOwner` → `DomainError.NotFound, "No such user."` (the caller passes false for blocked pairs too — one indistinguishable error, D-202) → duplicate row (Duplicate) → cap (LimitExceeded) → add row + Raise `FriendGroupInviteSent`. Name-uniqueness-per-owner is a DB constraint + Application concern, not aggregate state.
- [ ] **Step 4: Run** `dotnet test tests/PartyUp.Domain.Tests --filter "FullyQualifiedName~FriendGroupTests"` — PASS.
- [ ] **Step 5: Commit** `feat(domain): friend group aggregate with invite/accept membership`.

---

### Task 4: Block effects + deletion executor learn groups

**Files:**
- Modify: `src/PartyUp.Domain/Friendships/BlockEffects.cs`, `src/PartyUp.Domain/Users/DeletionExecution/DeletionExecutor.cs` (and its `DeletionScope`/result records), `src/PartyUp.Application/Friends/BlockEffectsExecutor.cs`, `src/PartyUp.Application/Deletion/ExecuteDeletionForUserUseCase.cs`
- Test: `tests/PartyUp.Domain.Tests/Friendships/BlockEffectsTests.cs`, `tests/PartyUp.Domain.Tests/Users/DeletionExecutorTests.cs` (extend existing files)

**Interfaces:**
- Produces: `BlockEffects.Apply(Block block, IReadOnlyCollection<Friendship> pairFriendships, IReadOnlyCollection<Session> sessions, IReadOnlyCollection<Poll> polls, IReadOnlyCollection<FriendGroup> groups, IClock clock)` — new `groups` param; for each group where one party is `OwnerId` and the other `Involves`, calls `group.RemoveCounterpartOf(...)` (member↔member pairs where neither owns are NOT in scope — mask-only, D-202). `DeletionScope` gains `IReadOnlyCollection<FriendGroup> OwnedGroups` + `IReadOnlyCollection<FriendGroup> MemberOfGroups`; `ContentRemovalResult` (not `DeletionExecutionResult` alone — a verifier finding moved the group-severing step into `ContentRemovalExecutor` itself so the D-180 admin ban path gets it too) gains `IReadOnlyCollection<FriendGroupId> DeletedGroups`, no separate removed-membership list — membership removals ride the tracked `FriendGroup` aggregates the same way session/poll participation removals do; `DeletionExecutionResult.DeletedGroups` is lifted straight from `ContentRemoval.DeletedGroups`. Step 4 deletes owned groups whole and the subject's rows in others' groups (D-209).
- Consumes: Task 3 aggregate. Scope providers (`IBlockEffectScopeProvider`, `IDeletionScopeProvider`) get the matching load methods in Task 5.

- [ ] **Step 1: Failing tests.** `BlockEffects` (owner blocks Accepted member → membership row gone `_D202`; owner blocks Invited → invite row gone `_D202`; member blocks owner → same; two non-owner members block → group untouched `_D202`). `DeletionExecutor` (subject's owned group listed in DeletedGroups; subject's membership in another's group removed — both `_D209`).
- [ ] **Step 2: Run** — FAIL (signature).
- [ ] **Step 3: Implement** domain fns; thread the new collections through `BlockEffectsExecutor.ExecuteAsync` (scope now carries groups; removals happen on tracked aggregates, EF persists) and `ExecuteDeletionForUserUseCase.ExecuteOnceAsync` (result applier deletes group rows; junction rows go via FK cascade). Both stay inside the existing single transactions. Silent: no notifications for these removals (D-207).
- [ ] **Step 4: Run** the two test files — PASS.
- [ ] **Step 5: Commit** `feat(domain): blocking and account deletion sever group membership`.

---

### Task 5: Persistence — EF configs, migration, SQL predicates

**Files:**
- Create: `src/PartyUp.Infrastructure/Persistence/Configurations/FriendGroupConfiguration.cs`, `src/PartyUp.Infrastructure/Persistence/Repositories/FriendGroupRepository.cs`, migration `AddFriendGroupsAndContentVisibility`
- Modify: `SessionConfiguration.cs`, `PollConfiguration.cs`, `AppDbContext.cs`, `SessionVisibilitySql.cs`, `PollVisibilitySql.cs`, `src/PartyUp.Infrastructure/Friends/BlockEffectScopeProvider.cs`, `.../DeletionScopeProvider` impl, `DeletionResultApplier.cs`

**Interfaces:**
- Produces: tables `friend_groups(id, owner_user_id, name, created_at, xmin)` with unique index `ux_friend_groups_owner_name` on `(owner_user_id, lower(name))`; `friend_group_members(group_id, user_id, status, invited_at, accepted_at)` PK `(group_id, user_id)`, check `status IN (0,1)`, FK cascade from group; `session_audience_groups(session_id, group_id)` + `poll_audience_groups(poll_id, group_id)` PKs on the pair, FK cascade both directions (group hard delete empties audiences, D-205; session/poll delete cleans up). Columns `sessions.visibility smallint NOT NULL` / `polls.visibility smallint NOT NULL` replacing `is_private`. Repository interface (`src/PartyUp.Application/Abstractions/IFriendGroupRepository.cs`):

```csharp
public interface IFriendGroupRepository
{
    void Add(FriendGroup group);
    void Remove(FriendGroup group);
    Task<FriendGroup?> FindNoTrackingAsync(FriendGroupId id, CancellationToken ct);
    Task<FriendGroup?> LoadForUpdateAsync(FriendGroupId id, CancellationToken ct);          // FOR NO KEY UPDATE on the group row
    Task<int> CountOwnedForUpdateAsync(UserId ownerId, CancellationToken ct);               // after caller locks the owner user row
    Task<IReadOnlyCollection<FriendGroupId>> LockViewerGroupIdsAsync(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds, CancellationToken ct);
        // ids from candidates where viewer is owner OR has an Accepted row; locks those membership/group rows (gate step, D-198)
    Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(UserId userId, CancellationToken ct);  // owner, Accepted, or Invited
}
```

- Consumes: Tasks 2–4 domain types.

- [ ] **Step 0 (added by verifier finding): CHECK constraints** `ck_sessions_visibility CHECK (visibility IN (0, 1, 2))` on sessions and `ck_polls_visibility` on polls — the engine fails closed on an unknown level, the DB must never store one.
- [ ] **Step 1: EF configs.** `FriendGroupConfiguration` follows `FriendshipConfiguration` verbatim patterns: snake_case names, `TypedIdConverter<FriendGroupId>`, `PropertyAccessMode.Field`, `HasConversion<short>()` for status, `timestamp with time zone`, `xmin` row version, Restrict FK to users, check constraints `ck_friend_group_members_status IN (0,1)` and `ck_friend_group_members_accept_time` mirroring the friendship accept-time check. Members map as owned collection (`OwnsMany` → table `friend_group_members`). `SessionConfiguration`: rename the `is_private` property mapping to `Visibility` (`HasConversion<short>()`, column `visibility`); map `_audienceGroupIds` as a `List<FriendGroupId>` join table via `builder.OwnsMany` or a keyless owned entity — use the same idiom the Session already uses for its simplest owned collection; Poll mirrors.
- [ ] **Step 2: Migration.** `dotnet dotnet-ef migrations add AddFriendGroupsAndContentVisibility --project src/PartyUp.Infrastructure --startup-project src/PartyUp.Api` (local codegen, no DB). Then hand-edit `Up()` so the column change is a conversion, not drop/add:

```csharp
migrationBuilder.AddColumn<short>(name: "visibility", table: "sessions", type: "smallint", nullable: false, defaultValue: (short)0);
migrationBuilder.Sql("UPDATE sessions SET visibility = CASE WHEN is_private THEN 1 ELSE 0 END;");
migrationBuilder.DropColumn(name: "is_private", table: "sessions");
```

(same for `polls`; then drop the temporary `defaultValue` by altering to no-default if the generator left one). **Do not run it against any DB** — CI/prod apply it (cardinal rule 1; `MigrateOnStartup`).
- [ ] **Step 3: SQL predicates.** In `SessionVisibilitySql` BOTH branches: `NOT s.is_private` → `s.visibility = 0`, and add the 2g arm inside the same OR-block that holds the two rule-2 EXISTS arms:

```sql
OR (
    s.visibility = 2
    AND EXISTS (
        SELECT 1 FROM session_audience_groups sag
        JOIN friend_groups fg ON fg.id = sag.group_id
        WHERE sag.session_id = s.id
          AND (fg.owner_user_id = {0}
               OR EXISTS (
                   SELECT 1 FROM friend_group_members fgm
                   WHERE fgm.group_id = sag.group_id AND fgm.user_id = {0} AND fgm.status = 1
               ))
    )
)
```

Structurally: rule-2 arms get wrapped as `(s.visibility = 0 AND (<the two existing EXISTS arms>))`, then `OR (<the 2g arm above>)`. Apply identically to `PollVisibilitySql.InheritedOnly` (aliases `p`/`poll_audience_groups pag`); `DirectAccess` is untouched (rule 1 only). Delete the stale "No S2 caller" comment at `SessionVisibilitySql.cs:21`. Note in the fragment doc comment: the 2g arm intentionally has no per-extender block check — rule 4 (viewer↔organizer) is the only block dimension for group grants; owner↔member blocks can't reach here because Task 4 deleted the membership.
- [ ] **Step 4: Repository + scope providers.** `FriendGroupRepository` implements the interface (lock statements follow `SessionLockRepository`'s `ExecuteSqlInterpolatedAsync` + `FOR NO KEY UPDATE` idiom, membership locks `FOR UPDATE` like `FriendPairLocker`). `BlockEffectScopeProvider.LoadForUpdateAsync`: also load-for-update groups where one party is owner and the other has any row. Deletion scope provider: load owned groups + membership groups. **The admin-ban scope provider must also load groups (D-180):** `ContentRemovalExecutor` now runs the D-209 group-severing step for both callers (verifier finding, `d35f5ce` follow-up), so whatever `IDeletionScopeProvider` implementation backs `BanAndRemoveContentUseCase`'s `LockedSubjectScopeRunner` has to populate `DeletionScope.OwnedGroups`/`MemberOfGroups` too, not only the deletion-sweep path — an empty pair there would silently make the ban path skip groups again. `DeletionResultApplier`: `context.FriendGroups.Remove(...)` per `DeletedGroups`, in `ApplyContentRemovalAsync` **only** (via `ContentRemovalResult.DeletedGroups` — the D-180 admin path never builds a `DeletionExecutionResult`); `ApplyAsync` inherits the deletion path's group hard-delete by delegating its removal tail to that method, and must not repeat it. **Applier note:** membership removals ride tracked aggregates (no removed-membership list on either result); the applier hard-deletes only `DeletedGroups` rows — the subject's own membership rows in others' groups are already gone from `FriendGroup.Members` on the tracked aggregates the executor mutated, and EF picks that up at the transaction's `SaveChanges` the same way it already does for session/poll participation removals.
- [ ] **Step 5: Build** `dotnet build src/PartyUp.Infrastructure` (Api still red until Task 8 — fine). `dotnet dotnet-ef migrations list --no-connect` shows the new migration. **Commit** `feat(infra): friend group persistence, visibility column, rule-2g SQL`.

---

### Task 6: Application — FriendGroup use cases + notifications

**Files:**
- Create: `src/PartyUp.Application/FriendGroups/{FriendGroupUseCases.cs,FriendGroupReadUseCases.cs,FriendGroupCommandResult.cs}` and read models
- Modify: `src/PartyUp.Domain/Notifications/` (NotificationKind + RecipientComputer), DI registration (same place existing use cases register)
- Test: `tests/PartyUp.Domain.Tests/Notifications/RecipientComputerTests.cs` (extend)

**Interfaces:**
- Produces (all on `FriendGroupUseCases`, primary-ctor DI `(IFriendGroupRepository groups, IUserRepository users, IFriendshipRepository friendships, IBlockRepository blocks, IFriendPairLocker pairLocker, IUserLockRepository userLocks, INotificationWriter notifications, IUnitOfWork unitOfWork, IClock clock)` — match the exact existing abstraction names from `SendFriendRequestUseCase`'s ctor when implementing):
  - `CreateAsync(UserId actor, string name, CancellationToken)` → `FriendGroupCommandResult` — locks actor's user row, `CountOwnedForUpdateAsync` ≥ 50 → `"group_limit_reached"` (D-208); duplicate name per owner (case-insensitive, catch the `ux_friend_groups_owner_name` violation like `ux_friendships_pair` is caught in UnitOfWork) → `"group_name_taken"`.
  - `RenameAsync`, `DeleteAsync` (owner-only; delete = `groups.Remove`, cascades; **before** removing, load sessions/polls whose audience contains the group and materialize the D-205 `AudienceGroupDeleted` notification per distinct organizer, in-transaction).
  - `InviteAsync(actor, groupId, IReadOnlyCollection<UserId> inviteeIds, ct)` — group `LoadForUpdateAsync` (cap serialization, D-208); per invitee: resolve friendship pair (`friendships.FindPairAsync`) and blocks; pass `isAcceptedFriendOfOwner: friendship is { IsAccepted: true } && noBlockEitherDirection` — blocked collapses into the same `"user_not_found"` NotFound (D-202); Raise → notification `FriendGroupInviteReceived` (D-207).
  - `AcceptInviteAsync` / `DeclineInviteAsync` (invitee actor; accept locks group row first — cap already counted the Invited row so accept never overflows; notification `FriendGroupInviteAccepted` to owner) / `RemoveMemberAsync` (owner-only; also serves as revoke — the target row's status, Invited or Accepted, decides which it means) / `LeaveAsync` (all silent, D-207).
  - Visibility: every read/command on a group the actor doesn't `Involves` returns `"group_not_found"` — fail-closed 404 (D-199). `Invited` actors may read name+owner and accept/decline only.
- Reads (`FriendGroupReadUseCases`): `ListMineAsync(actor)` → owned + member-of + pending-invites read models; `GetAsync(actor, groupId)` → detail (roster only when Accepted/owner).
- `NotificationKind`: append `FriendGroupInviteReceived`, `FriendGroupInviteAccepted`, `AudienceGroupDeleted` (append-only enum). `RecipientComputer.ForFriendGroupEvent(IDomainEvent, RecipientFilter)` maps the two events; `AudienceGroupDeleted` notifications are built directly in `DeleteAsync` (no domain event needed — the audience lives on sessions/polls, not the group).
- Consumes: Task 5 repository, Task 3 aggregate, existing `IUnitOfWork`/locker/notification patterns (`SendFriendRequestUseCase.cs:119` materialization shape).

- [ ] **Step 1: Failing RecipientComputer tests** (`ForFriendGroupEvent_SendsInviteToTheInvitee_D207`, `..._SendsAcceptanceToTheOwner_D207`).
- [ ] **Step 2: Run** — FAIL. **Step 3: Implement** kinds + computer + use cases (every command wrapped in `unitOfWork.ExecuteAsync`, mirror `SessionParticipationUseCases` shape; results as `FriendGroupCommandResult.Ok/Failed(code)`).
- [ ] **Step 4: Run** Domain.Tests notification filter — PASS. `dotnet build src/PartyUp.Application` green.
- [ ] **Step 5: Commit** `feat(app): friend group commands, reads, and notifications`.

---

### Task 7: Application — session/poll visibility wiring

**Files:**
- Modify: `src/PartyUp.Application/Sessions/{SessionActorVisibilityGate.cs,SessionReadUseCases.cs,SessionJoinRequestUseCases.cs}`, session create/edit use cases, `src/PartyUp.Application/Polls/{PollActorVisibilityGate.cs,PollReadUseCases.cs,PollJoinRequestUseCases.cs}`, poll create + finalization use cases

**Interfaces:**
- Produces: gates take `IFriendGroupRepository` and, **after** step 4's parent lock, when `session.Visibility == Groups`: `viewerGroupIds = await groups.LockViewerGroupIdsAsync(actorId, session.AudienceGroupIds, ct)`, then `Viewer.Create(actorId, friendIds, blockedIds, viewerGroupIds)` instead of `Viewer.From` (spec: membership resolved under the lock, never probe-time). Every `Viewer.Create` read-path site listed in the fact sheet (`SessionReadUseCases.cs:115,124`, `PollReadUseCases.cs:184,195`, join-request cases, `UserGraphResolver.cs:122`) gains the group-ids argument when the target is Groups-level (read paths may resolve without locking). Create/edit commands: `visibility` + `audienceGroupIds` flow in; authority check per **newly added** group only (D-201): each added id must satisfy `LockViewerGroupIdsAsync(actor, added)` contains it, else `"group_not_found"` (fail-closed — non-membership and nonexistence identical). Narrowing edits reuse the existing D-047 retention path (no roster changes needed — engine handles). Finalization: `Poll.Finalize` already copies audience (Task 2); the finalization use case persists the session's junction rows like any owned collection.
- Consumes: Tasks 2, 5, 6.

- [ ] **Step 1:** Extend gates + read paths + create/edit + finalization as above. The D-129 default in the create use case becomes `ContentVisibility.Public`.
- [ ] **Step 2:** `dotnet build src/PartyUp.Application` — green, `dotnet test tests/PartyUp.Domain.Tests` still green.
- [ ] **Step 3: Commit** `feat(app): groups visibility through gates, creates, edits, finalization`.

---

### Task 8: API — endpoints + contracts

**Files:**
- Create: `src/PartyUp.Api/Endpoints/FriendGroupEndpoints.cs`, `src/PartyUp.Api/Contracts/FriendGroups/FriendGroupContracts.cs`
- Modify: `src/PartyUp.Api/Contracts/Sessions/SessionContracts.cs`, `.../Polls/PollContracts.cs`, `Program.cs` (`api.MapFriendGroupEndpoints();` next to :385), `ApiOperationIds`

**Interfaces:**
- Produces routes (group under `RequireOnboarded`, tier helpers Common/Unsafe/BodyOperation, `{groupId:guid}` constraints, `CommandProblem` code→status mapping incl. `"group_not_found"`→404, `"group_limit_reached"`/`"group_member_limit_reached"`/`"group_name_taken"`→409):
  - `GET /api/v1/friend-groups` → `FriendGroupListResponse { owned: [...], memberOf: [...], invites: [...] }`
  - `POST /api/v1/friend-groups` `{ name }` → 201 `FriendGroupResponse`
  - `GET /api/v1/friend-groups/{groupId}` → detail (roster for owner/Accepted; name+owner for Invited)
  - `PATCH /api/v1/friend-groups/{groupId}` `{ name }` · `DELETE /api/v1/friend-groups/{groupId}`
  - `POST /api/v1/friend-groups/{groupId}/members` `{ userIds: [] }` (invite) · `DELETE /api/v1/friend-groups/{groupId}/members/{userId}` (owner: remove/revoke; self: leave)
  - `POST /api/v1/friend-groups/{groupId}/invitation/accept` · `.../decline`
- Session/poll contracts: `CreateSessionRequest`/`CreatePollRequest` replace `isPrivate: bool` with `visibility: "private" | "groups" | "public"` + `audienceGroupIds: Guid[]` (empty unless groups); edit endpoints likewise. Read models add `visibility` and provenance per D-206: `viaGroup: { id, name } | null` (populated ONLY when the engine grant for this viewer is `Group` and from the live group row) and `sharedWithGroup: bool` (true for any non-`Group`-grant viewer of a Groups target). Group names never appear otherwise. Sealed records, `[JsonUnmappedMemberHandling(Disallow)]`, `From(...)` mappers — the existing conventions. No web-only conveniences (mobile contract).
- Consumes: Task 6/7 use cases.

- [ ] **Step 1:** Implement endpoints + contracts, register in Program.cs.
- [ ] **Step 2:** `dotnet build PartyUp.sln` — **first solution-green build since Task 2**; fix all remaining `IsPrivate` fallout (Api mappers, seeders, tests' compile errors — mechanical mapping only, no behavior edits).
- [ ] **Step 3:** `dotnet test PartyUp.sln --filter "Category!=Integration"` — green.
- [ ] **Step 4: Commit** `feat(api): friend group endpoints and visibility contracts`.

---

### Task 9: Backend integration tests (Docker/WSL)

**Files:**
- Create: `tests/PartyUp.Api.IntegrationTests/FriendGroups/{FriendGroupFlowTests.cs,GroupVisibilitySqlTests.cs,GroupLifecycleTests.cs}`
- Modify: `tests/PartyUp.Api.IntegrationTests/Sessions/SessionVisibilitySqlCharacterizationTests.cs` (extend seeding)

**Interfaces:**
- Consumes: `PartyUpApiFactory` (`SeedUserAsync`, `LinkFriendsAsync`, `BlockAsync`, `MutableClock`), the Task 8 endpoints.

- [ ] **Step 1: Write the suites** (all `[Trait("Category","Integration")]`, factory pattern from the fact sheet):
  - `GroupVisibilitySqlTests`: seed owner+member+friend-of-member+stranger; one Groups session; assert `ListUpcomingAsync` admits owner+member only — **friend-of-extender exclusion is the leak regression** (`_D198`); Discover (`InheritedOnly`) shows it to the member (`_D198`); `visibility=0` gate: Public session still inherits, Private still doesn't (`_D018`, `_D043`); poll twin for `PollVisibilitySql` (`_D198`); empty-audience session visible to rule-1 holders only (`_D205`).
  - `FriendGroupFlowTests` (HTTP): create→invite→accept→target session→member sees/requests join (D-007 flow, `_D198`); non-member GET group → 404 body identical to nonexistent id (`_D199`); invite a blocked friend → same problem code as unknown user (`_D202`); caps: 51st group → 409 `group_limit_reached` (`_D208`).
  - `GroupLifecycleTests`: block owner↔member → membership gone, member loses the Groups session but a Going RSVP keeps rule-1 access (`_D202`, `_D203`); delete group → organizer notification exists + session degrades (`_D205`); Groups→Public edit deletes junctions (flip back requires re-pick, `_D203`); poll finalization copies audience (`_D204`); unfriend does NOT remove membership (`_D200`).
- [ ] **Step 2: Run in WSL:** `wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && dotnet test tests/PartyUp.Api.IntegrationTests -p:ArtifactsPath=/tmp/partyup-artifacts --filter FullyQualifiedName~FriendGroup"` (redirect to a log file per the long-run pattern) — PASS. Migration applies to the test DB automatically here; that is the sanctioned execution path.
- [ ] **Step 3:** Update TRACEABILITY.md verification cells for every D-number these tests carry. **Commit** `test(integration): friend group visibility, lifecycle, and cap coverage`.

---

### Task 10: Web — groups feature port

**Files:**
- Create: `web/src/features/groups/{GroupsPage.tsx,GroupDetailPage.tsx,GroupBits.tsx,groupQueries.ts}`, `web/src/i18n/{en,pt-BR,es}/groups.json`
- Modify: `web/src/App.tsx` (routes `/groups`, `/groups/:groupId` inside `AuthenticatedShell`), `web/src/components/shell/navItems.ts` (+ Groups item), `web/src/i18n/index.ts` (register namespace)

**Interfaces:**
- Consumes: regenerated `src/api/schema.d.ts` (**start the API in a background terminal, then `npm run gen:api`** — first step of this task), Lovable sources as the design reference, read directly via the Lovable MCP (`read_file`, project `54690dbe-480e-402a-8536-a7e60a364ae3`): `src/routes/groups.index.tsx`, `src/routes/groups.$groupId.tsx`, `src/components/groups/GroupBits.tsx`, `src/lib/groups-data.ts`.
- Produces: `groupQueries.ts` following `sessionQueries.ts` exactly — key factory `groupKeys = { list: ['friend-groups'] as const, detail: (id: string) => ['friend-group', id] as const }`, typed fetchers over `client.GET/POST/PATCH/DELETE('/api/v1/friend-groups...')`, mutations invalidate `groupKeys.list`. `GroupBits.tsx` port of the Lovable component set (Avatar reuses the repo's existing `AvatarDisc` from features/friends — do NOT port Lovable's initials Avatar; AvatarStack/Tag/FriendMultiSelect/NewGroupDialog ported with repo idioms: `SegmentedGroup` where applicable, dialog per the `SessionDiscardDialog` pattern, `raisedInteractiveClasses` on submits, h-11 targets, every string through `t()`).

- [ ] **Step 1:** `npm run gen:api` against the running Task-8 API; commit the schema.
- [ ] **Step 2:** Port pages. Adaptations (the only sanctioned deviations from the Lovable reference): mock store → TanStack Query; `person()` directory → API read models; hardcoded strings → `groups.json` keys (write en, then pt-BR and es honoring the established glossary — pt-BR uses the ratified terms: "pedido para entrar", "Quem vai" etc. where they surface); Lovable's 6-tab bottom bar → follow `navItems.ts`'s existing structure; empty states use the shell `EmptyState`.
- [ ] **Step 3: Vitest:** `web/src/features/groups/GroupsPage.test.tsx` — renders owned/member/invites sections from mocked queries; accept-invite fires mutation; `GroupDetailPage.test.tsx` — owner sees remove/rename/delete, member sees leave only (name it `...owner-only management_D199`).
- [ ] **Step 4:** `npm run lint && npm test && npm run build` — green. **Commit** `feat(web): friend groups pages`.

---

### Task 11: Web — visibility control, provenance tags, unfriend dialog

**Files:**
- Create: `web/src/features/groups/VisibilityControl.tsx`, `web/src/features/groups/ShareTag.tsx`
- Modify: `web/src/features/sessions/lovable/{SessionFormBits.tsx,SessionCreatePage.tsx,SessionEditPage.tsx,SessionBits.tsx or SessionSummaryCard.tsx}`, `web/src/features/polls/lovable/PollCreatePage.tsx`, `web/src/features/friends/FriendsPage.tsx` (unfriend flow), i18n `sessions.json`/`polls.json`/`friends.json` ×3 locales

**Interfaces:**
- Consumes: Lovable references `src/components/groups/GroupBits.tsx` (VisibilityControl), `src/components/groups/ShareTag.tsx`, `src/routes/friends.tsx` (dialog variant); Task 10 `groupQueries`.
- Produces: `VisibilityControl` typed on the generated contract — value `'private' | 'groups' | 'public'`, replaces `SessionPrivacyControl` (SessionFormBits.tsx:488) and the poll `SegmentedGroup` (PollCreatePage.tsx:767); when `groups`, chip multi-select from `groupKeys.list`, ≥1 validation surfaced through the page's existing `fieldErrors` map. `ShareTag share={...}` port: named variant only when the response's `viaGroup` is non-null; neutral when `sharedWithGroup`; nothing otherwise (the repo has no "Friends" tag concept — drop Lovable's third variant, it isn't in the spec).

- [ ] **Step 1:** Implement + wire; create-form default stays Public (D-129).
- [ ] **Step 2:** Unfriend flow: fetch `groupsContaining` equivalent from the list query client-side; render the "will remain in: …" note + "Also remove from my groups" checkbox (checked default, per the Lovable reference); on confirm with the box checked, fire the member-remove mutations after the unfriend mutation (`_D210` vitest: dialog lists group names; unchecked box skips removals).
- [ ] **Step 3:** Vitest: visibility control renders 3 options; groups with zero selection blocks submit with the i18n message (`_D201`); ShareTag masks name when `viaGroup` null (`_D206`).
- [ ] **Step 4:** `npm run lint && npm test && npm run build`; i18n key-parity test green. **Commit** `feat(web): visibility control, group provenance, unfriend surfacing`.

---

### Task 12: Browser lane + closing gate

**Files:**
- Create: `tests/PartyUp.BrowserTests/FriendGroups/GroupFlowTests.cs`
- Modify: `TRACEABILITY.md`, `IMPLEMENTATION_PLAN.md` (phase entry)

- [x] **Step 1:** Browser test (fixture pattern from `SessionRegisteredFlowTests`): owner seeds two users + friendship, creates group via UI, invites friend; friend accepts; owner creates Groups session; friend sees it on their upcoming list, and the D-206 "via" tag on the session detail page (`SessionListPage` carries no provenance tag in the shipped UI, so the tag assertion runs where `ShareTag` actually renders); stranger gets neither the list entry nor direct access (`CreateGroupInviteAcceptAndTarget_ShowsViaTagToMembers_D198_D206`).
- [x] **Step 2:** Full gate: `wsl -d Ubuntu-24.04 -e bash -lc "cd /mnt/c/Users/nilot/source/repos/PartyUpSocial && scripts/wsl-gate.sh"` — all lanes green. `scripts/wsl-gate.sh` carries no web or browser lane (rerun red web/browser lanes SOLO per the act recipe before treating as real).
- [x] **Step 3:** TRACEABILITY final sweep (every D-198..D-210 row cites its tests; regenerate the counts by grepping `_D\d\d\d`). IMPLEMENTATION_PLAN.md gains the Friend Groups phase entry marked complete.
- [x] **Step 4: Commit** `test(browser): friend group end-to-end flow` — then STOP. Push only after the owner's explicit go (mandatory WSL gate already run).

---

## Self-review record

- Spec §1–§10 all mapped: §2→Tasks 3/4, §3→Tasks 2/5/7, §4→Tasks 2/4/7/9, §5→Tasks 8/11, §6→Task 6, §7→Tasks 5/8, §8→Tasks 10/11, §9→Task 1, §10→Tasks 2/9/12.
- Type consistency: `ContentVisibility`, `FriendGroupId`, `VisibilityResult.Group(FriendGroupId)`, `LockViewerGroupIdsAsync`, `viaGroup`/`sharedWithGroup` used with identical spellings across tasks.
- Known cross-task ordering: solution build is red from Task 2 until Task 8 Step 2 (enum swap fallout) — intentional, per-task commits still land on the feature branch.
