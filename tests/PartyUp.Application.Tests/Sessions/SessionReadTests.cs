using PartyUp.Application.Sessions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionReadTests
{
    [Fact]
    public async Task HiddenAndAbsentAreEquivalent_D007_D019_D065()
    {
        SessionTestFixture fixture = new();
        Session privateSession = fixture.SeedSession(
            visibility: ContentVisibility.Private, invitees: [], organizerId: fixture.OrganizerId);
        SessionReadUseCases reads = fixture.ReadUseCases();
        UserId stranger = UserId.New();

        // Missing session: null.
        Assert.Null(await reads.GetSessionDetailAsync(SessionId.New(), stranger, default));

        // Hidden (private, stranger has no access): the same null, not a different signal.
        Assert.Null(await reads.GetSessionDetailAsync(privateSession.Id, stranger, default));

        // A blocked would-be inherited viewer also sees nothing (D-065), even on a non-private session.
        Session nonPrivate = fixture.SeedSession(invitees: [fixture.InviteeId]);
        fixture.Reads.MakeFriends(stranger, fixture.OrganizerId);
        fixture.Reads.MakeBlocked(stranger, fixture.OrganizerId);
        Assert.Null(await reads.GetSessionDetailAsync(nonPrivate.Id, stranger, default));
    }

    [Fact]
    public async Task PrivacyFlipRetainsParticipantsAndOutstandingRequests_D043_D047()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(invitees: [fixture.InviteeId]);
        EditSessionUseCase editUseCase = fixture.EditUseCase();

        await editUseCase.ExecuteAsync(
            new EditSessionCommand(
                session.Id, fixture.OrganizerId, null, null, null, null, null, null, null, null,
                ContentVisibility.Private, null, null),
            default);

        Assert.Equal(ContentVisibility.Private, session.Visibility);

        SessionReadUseCases reads = fixture.ReadUseCases();
        // The invited participant keeps direct access after the flip to private (rule 1).
        SessionDetail? detail = await reads.GetSessionDetailAsync(session.Id, fixture.InviteeId, default);
        Assert.NotNull(detail);
    }

    [Fact]
    public async Task AnyoneWithAccessSeesNotesAndTheCompleteRoster_D114()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(invitees: [fixture.InviteeId]);
        SessionReadUseCases reads = fixture.ReadUseCases();

        SessionDetail? detail = await reads.GetSessionDetailAsync(session.Id, fixture.InviteeId, default);

        Assert.NotNull(detail);
        Assert.Equal(session.Notes.Value, detail.Notes.Value);
        Assert.Equal(session.Participations.Count, detail.Roster.Count);
    }

    [Fact]
    public async Task HistoryHoldsOnlyCompletedFinalRosters_D095()
    {
        SessionTestFixture fixture = new();
        Session completed = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        Session stillUpcoming = fixture.SeedSession(minAttendance: 1, maxCapacity: null);

        fixture.Clock.Advance(
            SessionTestFixture.DefaultStart - SessionTestFixture.Origin + NodaTime.Duration.FromHours(3));
        completed.ApplyDueTransitions(fixture.Clock);
        _ = completed.DequeueEvents();
        Assert.Equal(SessionStatus.Completed, completed.Status);

        SessionReadUseCases reads = fixture.ReadUseCases();
        SessionPage<SessionSummary> history = await reads.ListCompletedHistoryAsync(
            fixture.OrganizerId, null, 20, default);

        Assert.Contains(history.Items, s => s.Id == completed.Id);
        Assert.DoesNotContain(history.Items, s => s.Id == stillUpcoming.Id);
    }

    [Fact]
    public async Task UpcomingKeepsInProgressAndExcludesTerminal_A067()
    {
        SessionTestFixture fixture = new();
        Session inProgress = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        Session canceled = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        canceled.Cancel(fixture.Clock);
        _ = canceled.DequeueEvents();

        fixture.Clock.Advance(
            SessionTestFixture.DefaultStart - SessionTestFixture.Origin + NodaTime.Duration.FromMinutes(5));

        SessionReadUseCases reads = fixture.ReadUseCases();
        SessionPage<SessionSummary> upcoming = await reads.ListUpcomingSessionsAsync(
            fixture.OrganizerId, null, 20, default);

        Assert.Contains(upcoming.Items, s => s.Id == inProgress.Id);
        Assert.DoesNotContain(upcoming.Items, s => s.Id == canceled.Id);
    }

    /// <summary>
    /// D-206: the "via &lt;group&gt;" tag's source is membership, not the resolved grant. A viewer who
    /// is a member of the targeted group gets the tag even though they also hold rule-1 access as the
    /// organizer; a directly invited viewer who is NOT in the group gets no tag at all — the
    /// Visibility==Groups/ViaGroup==null combination the wire layer renders as the neutral
    /// "sharedWithGroup" state.
    /// </summary>
    [Fact]
    public async Task ViaGroupIsMembershipBased_NotGrantBased_D206()
    {
        SessionTestFixture fixture = new();

        UserId groupOwnerId = UserId.New();
        FriendGroup group = FriendGroup.Create(groupOwnerId, GroupName.Create("Study Group"), fixture.Clock);
        group.Invite(groupOwnerId, fixture.OrganizerId, isAcceptedFriendOfOwner: true, fixture.Clock);
        group.AcceptInvite(fixture.OrganizerId, fixture.Clock);
        _ = group.DequeueEvents();
        fixture.Groups.Items.Add(group);

        SessionDetails details = new(
            SessionTitle.Create("Groups-shared session"),
            SessionTestFixture.DefaultStart,
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Groups);
        Session session = Session.Create(
            fixture.OrganizerId, details, [fixture.InviteeId], fixture.Clock, audienceGroupIds: [group.Id]);
        _ = session.DequeueEvents();
        fixture.Sessions.Add(session);

        SessionReadUseCases reads = fixture.ReadUseCases();

        // The organizer also holds an Accepted row in the targeted group: membership-based, so ViaGroup
        // still populates despite their broader rule-1 access.
        SessionDetail? organizerView = await reads.GetSessionDetailAsync(session.Id, fixture.OrganizerId, default);
        Assert.NotNull(organizerView);
        Assert.Equal(ContentVisibility.Groups, organizerView.Visibility);
        Assert.NotNull(organizerView.ViaGroup);
        Assert.Equal(group.Id, organizerView.ViaGroup!.GroupId);
        Assert.Equal(group.Name.Value, organizerView.ViaGroup.Name.Value);

        // The direct invitee holds rule-1 access (invited) but no row in the targeted group at all.
        SessionDetail? inviteeView = await reads.GetSessionDetailAsync(session.Id, fixture.InviteeId, default);
        Assert.NotNull(inviteeView);
        Assert.Equal(ContentVisibility.Groups, inviteeView.Visibility);
        Assert.Null(inviteeView.ViaGroup);
    }

    /// <summary>
    /// D-201: the organizer's own detail read carries the edit-prefill audience for a Groups-level
    /// session, ids and live names both, so the edit surface never re-derives the audience from
    /// scratch. Membership is Accepted, so both id and name come back.
    /// </summary>
    [Fact]
    public async Task OrganizerOfGroupsSessionGetsAudienceIdsAndNames_D201()
    {
        SessionTestFixture fixture = new();

        FriendGroup group = FriendGroup.Create(fixture.OrganizerId, GroupName.Create("Raid Crew"), fixture.Clock);
        _ = group.DequeueEvents();
        fixture.Groups.Items.Add(group);

        SessionDetails details = new(
            SessionTitle.Create("Groups-shared session"),
            SessionTestFixture.DefaultStart,
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Groups);
        Session session = Session.Create(
            fixture.OrganizerId, details, [], fixture.Clock, audienceGroupIds: [group.Id]);
        _ = session.DequeueEvents();
        fixture.Sessions.Add(session);

        SessionReadUseCases reads = fixture.ReadUseCases();
        SessionDetail? organizerView = await reads.GetSessionDetailAsync(session.Id, fixture.OrganizerId, default);

        Assert.NotNull(organizerView);
        Assert.NotNull(organizerView.AudienceGroups);
        AudienceGroupEntry entry = Assert.Single(organizerView.AudienceGroups!);
        Assert.Equal(group.Id, entry.GroupId);
        Assert.Equal(group.Name.Value, entry.Name!.Value);
    }

    /// <summary>
    /// D-206: an organizer who has since left a targeted group still gets that group's id (D-201/D-203
    /// retained targeting) but its name comes back null — the same owner/Accepted guard <c>ViaGroup</c>
    /// uses, applied per audience entry.
    /// </summary>
    [Fact]
    public async Task OrganizerWhoLeftTargetedGroupGetsIdWithNullName_D206()
    {
        SessionTestFixture fixture = new();

        UserId groupOwnerId = UserId.New();
        FriendGroup group = FriendGroup.Create(groupOwnerId, GroupName.Create("Old Crew"), fixture.Clock);
        group.Invite(groupOwnerId, fixture.OrganizerId, isAcceptedFriendOfOwner: true, fixture.Clock);
        group.AcceptInvite(fixture.OrganizerId, fixture.Clock);
        group.Leave(fixture.OrganizerId);
        _ = group.DequeueEvents();
        fixture.Groups.Items.Add(group);

        SessionDetails details = new(
            SessionTitle.Create("Groups-shared session"),
            SessionTestFixture.DefaultStart,
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Groups);
        Session session = Session.Create(
            fixture.OrganizerId, details, [fixture.InviteeId], fixture.Clock, audienceGroupIds: [group.Id]);
        _ = session.DequeueEvents();
        fixture.Sessions.Add(session);

        SessionReadUseCases reads = fixture.ReadUseCases();
        SessionDetail? organizerView = await reads.GetSessionDetailAsync(session.Id, fixture.OrganizerId, default);

        Assert.NotNull(organizerView);
        Assert.NotNull(organizerView.AudienceGroups);
        AudienceGroupEntry entry = Assert.Single(organizerView.AudienceGroups!);
        Assert.Equal(group.Id, entry.GroupId);
        Assert.Null(entry.Name);
    }

    /// <summary>
    /// D-206: the edit-prefill audience is organizer-only — a non-organizer member reading the same
    /// Groups-level session gets a null field entirely, not a redacted list.
    /// </summary>
    [Fact]
    public async Task NonOrganizerMemberGetsNullAudienceGroups_D206()
    {
        SessionTestFixture fixture = new();

        FriendGroup group = FriendGroup.Create(fixture.OrganizerId, GroupName.Create("Raid Crew"), fixture.Clock);
        group.Invite(fixture.OrganizerId, fixture.InviteeId, isAcceptedFriendOfOwner: true, fixture.Clock);
        group.AcceptInvite(fixture.InviteeId, fixture.Clock);
        _ = group.DequeueEvents();
        fixture.Groups.Items.Add(group);

        SessionDetails details = new(
            SessionTitle.Create("Groups-shared session"),
            SessionTestFixture.DefaultStart,
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Groups);
        Session session = Session.Create(
            fixture.OrganizerId, details, [fixture.InviteeId], fixture.Clock, audienceGroupIds: [group.Id]);
        _ = session.DequeueEvents();
        fixture.Sessions.Add(session);

        SessionReadUseCases reads = fixture.ReadUseCases();
        SessionDetail? memberView = await reads.GetSessionDetailAsync(session.Id, fixture.InviteeId, default);

        Assert.NotNull(memberView);
        Assert.Null(memberView.AudienceGroups);
    }
}
