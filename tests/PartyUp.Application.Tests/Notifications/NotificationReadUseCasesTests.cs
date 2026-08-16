using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Notifications;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Tests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §2: the notification-actor read projection. Every test drives the real
/// <see cref="IdentityMasking"/> and <see cref="VisibilityEngine"/> through
/// <see cref="NotificationReadUseCases"/> — no masking or visibility logic is reimplemented here.
/// </summary>
public sealed class NotificationReadUseCasesTests
{
    private static readonly Instant Origin = Instant.FromUtc(2026, 8, 9, 12, 0);

    // ---- MaskingReason outcomes, routed through the shipped IdentityMasking (friend-graph rows: no subject gate) ----

    [Fact]
    public async Task Actor_TombstonedRendersAnonymous_D092()
    {
        NotificationFixture fixture = new();
        User actor = fixture.MakeTombstoned("tomb-actor");
        fixture.Users.Users[actor.Id] = actor;
        UserId recipientId = fixture.MakeRecipient();

        Notification notification = Notification.Create(
            recipientId, NotificationType.FriendRequestReceived, NotificationSubject.ForActor(actor.Id), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.NotNull(row.Actor);
        Assert.True(row.Actor!.Value.IsAnonymous);
        Assert.Equal(MaskingReason.Tombstoned, row.Actor.Value.Reason);
        Assert.Equal(actor.Id.Value, row.ActorUserId); // §4 wire rule: tombstoned keeps actorUserId
    }

    [Fact]
    public async Task Actor_BlockedPairRendersAnonymous_D076()
    {
        NotificationFixture fixture = new();
        User actor = fixture.MakeActive("blocked-actor", "Blocked Actor");
        fixture.Users.Users[actor.Id] = actor;
        UserId recipientId = fixture.MakeRecipient();
        fixture.Blocks.Items.Add(Block.Create(recipientId, actor.Id, Username.Create("blocked-actor"), fixture.Clock));

        Notification notification = Notification.Create(
            recipientId, NotificationType.FriendRequestReceived, NotificationSubject.ForActor(actor.Id), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.NotNull(row.Actor);
        Assert.True(row.Actor!.Value.IsAnonymous);
        Assert.Equal(MaskingReason.Blocked, row.Actor.Value.Reason);
        Assert.Null(row.ActorUserId); // §4 wire rule: blocked-pair masking omits actorUserId too
    }

    [Fact]
    public async Task Actor_ProfileIncompleteRendersAnonymous()
    {
        NotificationFixture fixture = new();
        User actor = User.BeginOnboarding(
            UserId.New(), FriendCode.Create("23456789AB"), AgeGateSource.SelfAttested, fixture.Clock);
        fixture.Users.Users[actor.Id] = actor;
        UserId recipientId = fixture.MakeRecipient();

        Notification notification = Notification.Create(
            recipientId, NotificationType.FriendRequestReceived, NotificationSubject.ForActor(actor.Id), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.NotNull(row.Actor);
        Assert.True(row.Actor!.Value.IsAnonymous);
        Assert.Equal(MaskingReason.ProfileIncomplete, row.Actor.Value.Reason);
        Assert.Equal(actor.Id.Value, row.ActorUserId); // §4 wire rule: profile-incomplete keeps actorUserId
    }

    [Fact]
    public async Task Actor_NamedWhenReachableAndUnmasked()
    {
        NotificationFixture fixture = new();
        User actor = fixture.MakeActive("named-actor", "Named Actor");
        fixture.Users.Users[actor.Id] = actor;
        UserId recipientId = fixture.MakeRecipient();

        Notification notification = Notification.Create(
            recipientId, NotificationType.FriendRequestReceived, NotificationSubject.ForActor(actor.Id), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.NotNull(row.Actor);
        Assert.False(row.Actor!.Value.IsAnonymous);
        Assert.Equal("Named Actor", row.Actor.Value.DisplayName!.Value.Value);
        Assert.Equal(actor.Id.Value, row.ActorUserId); // §4 wire rule: a named actor keeps actorUserId
    }

    // ---- unreachable subject -> minimal form, over both fixtures (F2: each arm must be
    // discriminating — reachable without the masking cause, minimal with it) ----

    [Fact]
    public async Task UnreachableSubject_BlockedPair_MasksDespiteLiveParticipation_D076()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        UserId organizerId = UserId.New();
        // The recipient holds a live, direct invited participation (§7 rule 1) — without the block
        // below the session is reachable on that alone, so the block arm is what a deleted
        // `blocks.FindPairsAmongAsync` call would fail to catch.
        Session session = fixture.CreateSession(organizerId, invitees: [recipientId]);
        fixture.Sessions.Add(session);

        Notification notification = Notification.Create(
            recipientId, NotificationType.SessionCanceled, NotificationSubject.ForSession(session.Id), fixture.Clock);

        NotificationReadModel reachableRow = await fixture.UseCases().ProjectAsync(notification, recipientId, default);
        Assert.True(reachableRow.HasSubject);
        Assert.Equal(session.Id.Value, reachableRow.SubjectId);

        fixture.Blocks.Items.Add(Block.Create(recipientId, organizerId, Username.Create("blocked-organizer"), fixture.Clock));

        NotificationReadModel maskedRow = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.False(maskedRow.HasSubject);
        Assert.Equal(NotificationSubjectKind.None, maskedRow.SubjectKind);
        Assert.Null(maskedRow.SubjectId);
        Assert.Null(maskedRow.SubjectTitle);
        Assert.Null(maskedRow.Actor);
        Assert.Null(maskedRow.ActorUserId);
        Assert.True(maskedRow.IsUnread); // row stays present and unread-countable
    }

    [Fact]
    public async Task UnreachableSubject_RemovedParticipation_RendersMinimalForm()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        UserId organizerId = UserId.New();
        // A real `Removed` participation row (§5.2: terminal, keeps nothing) — proven reachable while
        // live, then unreachable once removed, no block or friendship involved either arm.
        Session session = fixture.CreateSession(organizerId, invitees: [recipientId]);
        fixture.Sessions.Add(session);

        Notification notification = Notification.Create(
            recipientId, NotificationType.SessionCanceled, NotificationSubject.ForSession(session.Id), fixture.Clock);

        NotificationReadModel liveRow = await fixture.UseCases().ProjectAsync(notification, recipientId, default);
        Assert.True(liveRow.HasSubject);
        Assert.Equal(session.Id.Value, liveRow.SubjectId);

        session.RemoveParticipation(ParticipantRef.ForUser(recipientId), fixture.Clock);

        NotificationReadModel departedRow = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.False(departedRow.HasSubject);
        Assert.Null(departedRow.SubjectId);
        Assert.Null(departedRow.SubjectTitle);
        Assert.Null(departedRow.Actor);
        Assert.Null(departedRow.ActorUserId);
        Assert.True(departedRow.IsUnread);
    }

    [Fact]
    public async Task ReachableSubject_RendersTitleAndId()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();

        // Organizer holds direct access unconditionally (§7 rule 1).
        Session session = fixture.CreateSession(recipientId, isPrivate: true);
        fixture.Sessions.Add(session);

        Notification notification = Notification.Create(
            recipientId, NotificationType.SessionCanceled, NotificationSubject.ForSession(session.Id), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.True(row.HasSubject);
        Assert.Equal(NotificationSubjectKind.Session, row.SubjectKind);
        Assert.Equal(session.Id.Value, row.SubjectId);
        Assert.Equal(session.Title.Value, row.SubjectTitle);
    }

    // ---- both-ref row: session unreachable falls back to the poll (F1) ----

    [Fact]
    public async Task BothRefRow_SessionUnreachablePollReachable_FallsBackToPollSubject()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        UserId organizerId = UserId.New();

        // The recipient keeps direct poll access (§7 rule 1 — Active, independent of their vote) but
        // was not carried onto the finalized session's roster — e.g. Unavailable throughout on the
        // winning option (§6.4-3) — so the session ref alone is unreachable for them.
        Poll poll = fixture.CreatePoll(organizerId, invitees: [recipientId]);
        fixture.Polls.Add(poll);
        Session session = fixture.CreateSession(organizerId, isPrivate: true);
        fixture.Sessions.Add(session);

        Instant selected = fixture.Clock.GetCurrentInstant() + Duration.FromHours(11);
        Notification notification = Notification.Create(
            recipientId, NotificationType.PollOptionSelected,
            NotificationSubject.ForSelectedOption(poll.Id, session.Id, selected), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.True(row.HasSubject);
        Assert.Equal(NotificationSubjectKind.Poll, row.SubjectKind);
        Assert.Equal(poll.Id.Value, row.SubjectId);
        Assert.Equal(poll.Title.Value, row.SubjectTitle);
        Assert.Equal(selected, row.OptionStartTime);
    }

    [Fact]
    public async Task BothRefRow_BothRefsUnreachable_RendersMinimalForm()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        UserId organizerId = UserId.New();

        // Neither the poll nor the session carries the recipient: minimal form only when every
        // carried ref is unreachable.
        Poll poll = fixture.CreatePoll(organizerId);
        fixture.Polls.Add(poll);
        Session session = fixture.CreateSession(organizerId, isPrivate: true);
        fixture.Sessions.Add(session);

        Instant selected = fixture.Clock.GetCurrentInstant() + Duration.FromHours(11);
        Notification notification = Notification.Create(
            recipientId, NotificationType.PollOptionSelected,
            NotificationSubject.ForSelectedOption(poll.Id, session.Id, selected), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.False(row.HasSubject);
        Assert.Equal(NotificationSubjectKind.None, row.SubjectKind);
        Assert.Null(row.SubjectId);
        Assert.Null(row.SubjectTitle);
    }

    // ---- recipient-zone rendering + fallback (D-060) ----

    [Fact]
    public async Task RecipientZoneAndLocale_RenderFromProfile_D060()
    {
        NotificationFixture fixture = new();
        User recipient = fixture.MakeActive("zoned", "Zoned", "America/Sao_Paulo", "pt");
        fixture.Users.Users[recipient.Id] = recipient;

        Notification notification = Notification.Create(
            recipient.Id, NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(fixture.MakeRecipient()), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipient.Id, default);

        Assert.Equal("pt-BR", row.RecipientLocale);
        Assert.Equal("America/Sao_Paulo", row.RecipientZone.Id);
    }

    [Fact]
    public async Task RecipientWithNoProfile_FallsBackToEnUtc_D060()
    {
        NotificationFixture fixture = new();
        UserId recipientId = UserId.New(); // never added to the user repository

        Notification notification = Notification.Create(
            recipientId, NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(fixture.MakeRecipient()), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.Equal("en", row.RecipientLocale);
        Assert.Equal(DateTimeZone.Utc, row.RecipientZone);
    }

    [Fact]
    public async Task RecipientWithNullLocaleAndZone_FallsBackToEnUtc_D060()
    {
        NotificationFixture fixture = new();
        // A real, registered User row (unlike the missing-row arm above) whose Locale and TimeZone
        // are both still null — an Onboarding account that never completed profile setup.
        User recipient = User.BeginOnboarding(
            UserId.New(), FriendCode.Create("34567890AB"), AgeGateSource.SelfAttested, fixture.Clock);
        fixture.Users.Users[recipient.Id] = recipient;

        Notification notification = Notification.Create(
            recipient.Id, NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(fixture.MakeRecipient()), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipient.Id, default);

        Assert.Equal("en", row.RecipientLocale);
        Assert.Equal(DateTimeZone.Utc, row.RecipientZone);
    }

    // ---- option instant: present for 14/19, absent otherwise ----

    [Fact]
    public async Task OptionStartTime_PresentForPollOptionSuggested_Type14()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        Poll poll = fixture.CreatePoll(recipientId);
        fixture.Polls.Add(poll);
        Instant suggested = fixture.Clock.GetCurrentInstant() + Duration.FromHours(10);

        Notification notification = Notification.Create(
            recipientId, NotificationType.PollOptionSuggested,
            NotificationSubject.ForPollOption(poll.Id, suggested), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.Equal(suggested, row.OptionStartTime);
    }

    [Fact]
    public async Task OptionStartTime_PresentForPollOptionSelected_Type19()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        Session session = fixture.CreateSession(recipientId, isPrivate: true);
        fixture.Sessions.Add(session);
        Poll poll = fixture.CreatePoll(recipientId);
        fixture.Polls.Add(poll);
        Instant selected = fixture.Clock.GetCurrentInstant() + Duration.FromHours(11);

        Notification notification = Notification.Create(
            recipientId, NotificationType.PollOptionSelected,
            NotificationSubject.ForSelectedOption(poll.Id, session.Id, selected), fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.Equal(selected, row.OptionStartTime);
    }

    [Fact]
    public async Task OptionStartTime_AbsentForOtherTypes_EvenWhenSubjectCarriesOne()
    {
        NotificationFixture fixture = new();
        UserId recipientId = fixture.MakeRecipient();
        Poll poll = fixture.CreatePoll(recipientId);
        fixture.Polls.Add(poll);
        Instant candidate = fixture.Clock.GetCurrentInstant() + Duration.FromHours(9);

        // PollClosed does not need an actor or an option instant; it is set here anyway to prove the
        // projection filters by type rather than trusting whatever the subject happens to carry.
        NotificationSubject subject = NotificationSubject.Create(null, poll.Id, null, candidate);
        Notification notification = Notification.Create(recipientId, NotificationType.PollClosed, subject, fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.Null(row.OptionStartTime);
    }

    // ---- D-207: a group ref masks by literal membership, not by collapsing the whole row ----------

    [Fact]
    public async Task GroupRef_MaskedWhenRecipientNoLongerInvolvesTheGroup_D207()
    {
        NotificationFixture fixture = new();
        UserId ownerId = fixture.MakeRecipient();
        UserId recipientId = fixture.MakeRecipient();

        FriendGroup group = FriendGroup.Create(ownerId, GroupName.Create("Raid Crew"), fixture.Clock);
        group.Invite(ownerId, recipientId, isAcceptedFriendOfOwner: true, fixture.Clock);
        group.AcceptInvite(recipientId, fixture.Clock);
        _ = group.DequeueEvents();
        // The recipient no longer Involves the group (removed after the invite/accept round trip) —
        // the group row itself still exists, so this is D-207's membership-lapsed case, distinct from
        // AudienceGroupDeleted's hard-delete case.
        group.RemoveMember(ownerId, recipientId);
        fixture.Groups.Items.Add(group);

        Notification notification = Notification.Create(
            recipientId,
            NotificationType.FriendGroupInviteAccepted,
            NotificationSubject.ForFriendGroupInvite(group.Id, ownerId),
            fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        // Masked: no subject id/title leaks the group's name to a viewer no longer in it.
        Assert.False(row.HasSubject);
        Assert.Null(row.SubjectId);
        Assert.Null(row.SubjectTitle);
        // Not suppressed: the row still renders (actor resolves), matching D-207's "must not suppress
        // the notification itself" — a group ref never forces the whole row to Minimal.
        Assert.NotNull(row.Actor);
        Assert.Equal(NotificationType.FriendGroupInviteAccepted, row.Type);
    }

    [Fact]
    public async Task GroupRef_VisibleWhenRecipientStillInvolvesTheGroup_D207()
    {
        NotificationFixture fixture = new();
        UserId ownerId = fixture.MakeRecipient();
        UserId recipientId = fixture.MakeRecipient();

        FriendGroup group = FriendGroup.Create(ownerId, GroupName.Create("Raid Crew"), fixture.Clock);
        group.Invite(ownerId, recipientId, isAcceptedFriendOfOwner: true, fixture.Clock);
        group.AcceptInvite(recipientId, fixture.Clock);
        _ = group.DequeueEvents();
        fixture.Groups.Items.Add(group);

        Notification notification = Notification.Create(
            recipientId,
            NotificationType.FriendGroupInviteAccepted,
            NotificationSubject.ForFriendGroupInvite(group.Id, ownerId),
            fixture.Clock);

        NotificationReadModel row = await fixture.UseCases().ProjectAsync(notification, recipientId, default);

        Assert.True(row.HasSubject);
        Assert.Equal(NotificationSubjectKind.Group, row.SubjectKind);
        Assert.Equal(group.Id.Value, row.SubjectId);
        Assert.Equal(group.Name.Value, row.SubjectTitle);
    }
}

// ---- shared in-memory test doubles for this file ------------------------------------------------

internal sealed class MutableClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;

    public void Advance(Duration duration) => current += duration;
}

internal sealed class FakeSessionRepository : ISessionRepository
{
    private readonly Dictionary<SessionId, Session> _sessions = [];

    public void Add(Session session) => _sessions[session.Id] = session;

    public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.TryGetValue(id, out Session? session) ? session : null);

    public Task<Session?> FindNoTrackingAsync(SessionId id, CancellationToken cancellationToken) =>
        FindAsync(id, cancellationToken);

    public Task<SessionForUpdate?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.TryGetValue(id, out Session? session)
            ? new SessionForUpdate(session, [], [])
            : null);

    public Task<IReadOnlyList<FutureEligibleSession>> ListFutureSessionsWithEligibleParticipationAsync(
        UserId userId,
        Instant now,
        (Instant StartTime, Guid Id)? afterCursor,
        int limit,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This test double never exercises the reminder-default replan page.");
}

internal sealed class FakePollRepository : IPollRepository
{
    private readonly Dictionary<PollId, Poll> _polls = [];

    public void Add(Poll poll) => _polls[poll.Id] = poll;

    public void RemoveOptionAndCascadedVotes(PollOption deletedOption, IReadOnlyList<PollVote> cascadedVotes)
    {
        // No-op: this fake never tracks options/votes as separate rows.
    }

    public void RemoveParticipationAndWithdrawnVotes(
        PollParticipant leftParticipant,
        IReadOnlyList<PollVote> withdrawnVotes)
    {
        // No-op: this fake never tracks participants/votes as separate rows.
    }

    public Task<Poll?> FindAsync(PollId id, CancellationToken cancellationToken) =>
        Task.FromResult(_polls.TryGetValue(id, out Poll? poll) ? poll : null);

    public Task<Poll?> FindNoTrackingAsync(PollId id, CancellationToken cancellationToken) =>
        FindAsync(id, cancellationToken);

    public Task<PollForUpdate?> LoadForUpdateAsync(PollId id, CancellationToken cancellationToken) =>
        Task.FromResult(_polls.TryGetValue(id, out Poll? poll) ? new PollForUpdate(poll, []) : null);
}

internal sealed class FakeFriendshipRepository : IFriendshipRepository
{
    internal List<Friendship> Items { get; } = [];

    public Task<Friendship?> FindPairAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(f => f.IsBetween(first, second)));

    public Task<Friendship?> FindPairForUpdateAsync(UserId first, UserId second, CancellationToken cancellationToken) =>
        FindPairAsync(first, second, cancellationToken);

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId, UserId actorId, CancellationToken cancellationToken) =>
        Task.FromResult<FriendshipPair?>(null);

    public Task<Friendship?> FindByIdForUpdateAsync(FriendshipId friendshipId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(f => f.Id == friendshipId));

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AcceptedFriendProjection>>([]);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PendingFriendProjection>>([]);

    public void Add(Friendship friendship) => Items.Add(friendship);

    public void Remove(Friendship friendship) => Items.Remove(friendship);
}

internal sealed class FakeBlockRepository : IBlockRepository
{
    internal List<Block> Items { get; } = [];

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Block>>(
            [.. Items.Where(b =>
                (b.BlockerId == first && b.BlockedId == second) ||
                (b.BlockerId == second && b.BlockedId == first))]);

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId, UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<BlockPair?>(null);

    public Task<Block?> FindByIdForUpdateAsync(BlockId blockId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(b => b.Id == blockId));

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BlockedUserProjection>>([]);

    public void Add(Block block) => Items.Add(block);

    public void Remove(Block block) => Items.Remove(block);
}

internal sealed class FakeFriendGroupRepository : IFriendGroupRepository
{
    internal List<FriendGroup> Items { get; } = [];

    public void Add(FriendGroup group) => Items.Add(group);

    public void Remove(FriendGroup group) => Items.Remove(group);

    public Task<FriendGroup?> FindNoTrackingAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(g => g.Id == id));

    public Task<FriendGroup?> LoadForUpdateAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(g => g.Id == id));

    public Task<int> CountOwnedForUpdateAsync(UserId ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Count(g => g.OwnerId == ownerId));

    public Task<IReadOnlyCollection<FriendGroupId>> LockViewerGroupIdsAsync(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds, CancellationToken cancellationToken) =>
        Task.FromResult(ViewerGroupIds(viewerId, candidateGroupIds));

    public Task<IReadOnlyCollection<FriendGroupId>> GetViewerGroupIdsAsync(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds, CancellationToken cancellationToken) =>
        Task.FromResult(ViewerGroupIds(viewerId, candidateGroupIds));

    public Task<GroupName?> FindNameAsync(FriendGroupId id, CancellationToken cancellationToken) =>
        Task.FromResult(Items.FirstOrDefault(g => g.Id == id)?.Name);

    public Task<IReadOnlyCollection<FriendGroup>> ListInvolvingAsync(UserId userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<FriendGroup>>([.. Items.Where(g => g.Involves(userId))]);

    public Task<IReadOnlyCollection<UserId>> ListDistinctOrganizersTargetingAsync(
        FriendGroupId groupId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<UserId>>([]);

    private IReadOnlyCollection<FriendGroupId> ViewerGroupIds(
        UserId viewerId, IReadOnlyCollection<FriendGroupId> candidateGroupIds) =>
        [.. candidateGroupIds.Where(id => Items.Any(g =>
            g.Id == id && (g.OwnerId == viewerId || g.HasAcceptedMember(viewerId))))];
}

/// <summary>Shared fixture wiring for this file's <see cref="NotificationReadUseCases"/> tests.</summary>
internal sealed class NotificationFixture
{
    internal NotificationFixture()
    {
        Clock = new MutableClock(Instant.FromUtc(2026, 8, 9, 12, 0));
        Sessions = new FakeSessionRepository();
        Polls = new FakePollRepository();
        Users = new FakeUserRepository();
        Friendships = new FakeFriendshipRepository();
        Blocks = new FakeBlockRepository();
        Groups = new FakeFriendGroupRepository();
    }

    internal MutableClock Clock { get; }

    internal FakeSessionRepository Sessions { get; }

    internal FakePollRepository Polls { get; }

    internal FakeUserRepository Users { get; }

    internal FakeFriendshipRepository Friendships { get; }

    internal FakeBlockRepository Blocks { get; }

    internal FakeFriendGroupRepository Groups { get; }

    internal NotificationReadUseCases UseCases() =>
        new(Sessions, Polls, Users, Friendships, Blocks, Groups);

    /// <summary>A minimal Active user, registered with the fake repository, used as the notification recipient.</summary>
    internal UserId MakeRecipient()
    {
        User recipient = MakeActive($"r{Guid.NewGuid():N}"[..12], "Recipient");
        Users.Users[recipient.Id] = recipient;
        return recipient.Id;
    }

    internal User MakeActive(
        string username, string displayName, string zoneId = "America/Fortaleza", string locale = "en")
    {
        User user = User.BeginOnboarding(
            UserId.New(), FriendCode.Create("23456789AB"), AgeGateSource.SelfAttested, Clock);
        user.CompleteOnboarding(
            Username.Create(username),
            DisplayName.Create(displayName),
            Avatar.FromEmoji("🎮"),
            DateTimeZoneProviders.Tzdb[zoneId],
            Locale.Create(locale),
            Clock);
        user.DequeueEvents();
        return user;
    }

    internal User MakeTombstoned(string username)
    {
        User user = MakeActive(username, "Gone");
        user.RequestDeletion(Clock);
        user.DequeueEvents();
        Clock.Advance(User.DeletionGracePeriod);
        user.MarkDeleted(Clock);
        user.DequeueEvents();
        return user;
    }

    internal Session CreateSession(UserId organizerId, IReadOnlyList<UserId>? invitees = null, bool isPrivate = false)
    {
        SessionDetails details = new(
            SessionTitle.Create("Raid night"),
            Clock.GetCurrentInstant() + Duration.FromHours(24),
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            1,
            null,
            isPrivate ? ContentVisibility.Private : ContentVisibility.Public);

        Session session = Session.Create(organizerId, details, invitees ?? [], Clock);
        session.DequeueEvents();
        return session;
    }

    internal Poll CreatePoll(UserId organizerId, IReadOnlyList<UserId>? invitees = null)
    {
        PollDetails details = new(
            SessionTitle.Create("Raid poll"),
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            1,
            null,
            visibility: ContentVisibility.Private);

        Instant now = Clock.GetCurrentInstant();
        Poll poll = Poll.Create(
            organizerId,
            details,
            PollDeadlineChoice.Hours24,
            [now + Duration.FromHours(6), now + Duration.FromHours(7)],
            invitees ?? [],
            Clock);
        poll.DequeueEvents();
        return poll;
    }
}
