using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Availability;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Application.Tests.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Polls;

/// <summary>
/// The poll detail read's per-option voter lists: each option's <c>AvailableVoters</c>/
/// <c>MaybeVoters</c>/<c>UnavailableVoters</c> is built off the exact same <see cref="PollRosterEntry"/>
/// rows the roster carries (see <see cref="PollVoterListBuilder"/>), so a masked voter's identity
/// agrees between the roster and every option list without a second masking pass.
/// </summary>
public sealed class PollReadUseCasesTests
{
    [Fact]
    public async Task GetPollDetail_PerOptionVoterLists_MatchMixedVotes()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        User a = FriendTestData.Active($"a{Guid.NewGuid():N}"[..12], "Alice");
        User b = FriendTestData.Active($"b{Guid.NewGuid():N}"[..12], "Bob");
        reads.Nameplates[organizer.Id.Value] = new UserNameplate(organizer.DisplayName, organizer.Avatar);
        reads.Nameplates[a.Id.Value] = new UserNameplate(a.DisplayName, a.Avatar);
        reads.Nameplates[b.Id.Value] = new UserNameplate(b.DisplayName, b.Avatar);

        Poll poll = NewPoll(organizer.Id, [a.Id, b.Id], clock);
        PollOptionId optionId = poll.Options[0].Id;
        PollOptionId otherOptionId = poll.Options[1].Id;

        poll.Vote(ParticipantRef.ForUser(a.Id), optionId, VoteChoice.Available, clock);
        poll.Vote(ParticipantRef.ForUser(b.Id), optionId, VoteChoice.Unavailable, clock);
        poll.Vote(ParticipantRef.ForUser(organizer.Id), otherOptionId, VoteChoice.Maybe, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, organizer.Id, default);

        Assert.NotNull(detail);
        PollOptionGridEntry option = detail!.Options.Single(o => o.Id == optionId);
        Assert.Equal(a.Id.Value, Assert.Single(option.AvailableVoters).Participant.UserId!.Value.Value);
        Assert.Equal(b.Id.Value, Assert.Single(option.UnavailableVoters).Participant.UserId!.Value.Value);
        Assert.Empty(option.MaybeVoters);

        PollOptionGridEntry otherOption = detail.Options.Single(o => o.Id == otherOptionId);
        Assert.Equal(organizer.Id.Value, Assert.Single(otherOption.MaybeVoters).Participant.UserId!.Value.Value);
        Assert.Empty(otherOption.AvailableVoters);
        Assert.Empty(otherOption.UnavailableVoters);
    }

    /// <summary>
    /// D-076: a blocked-pair voter is masked identically wherever it appears — the roster row and the
    /// option's voter-list row are the SAME <see cref="PollRosterEntry"/> instance (built once, off the
    /// roster loop), so <see cref="PollRosterEntry.ParticipationId"/>, the null display name, and the
    /// null avatar all agree between the two surfaces by construction, not by coincidence.
    /// </summary>
    [Fact]
    public async Task GetPollDetail_OptionVoterList_MasksBlockedPairSameAsRoster_D076()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User viewer = FriendTestData.Active($"v{Guid.NewGuid():N}"[..12], "Viewer");
        User blockedVoter = FriendTestData.Active($"x{Guid.NewGuid():N}"[..12], "Blocked Voter");
        reads.Nameplates[viewer.Id.Value] = new UserNameplate(viewer.DisplayName, viewer.Avatar);
        reads.Nameplates[blockedVoter.Id.Value] = new UserNameplate(blockedVoter.DisplayName, blockedVoter.Avatar);

        blocks.Items.Add(Block.Create(viewer.Id, blockedVoter.Id, blockedVoter.Username!.Value, clock));

        Poll poll = NewPoll(viewer.Id, [blockedVoter.Id], clock);
        PollOptionId optionId = poll.Options[0].Id;
        poll.Vote(ParticipantRef.ForUser(blockedVoter.Id), optionId, VoteChoice.Available, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, viewer.Id, default);

        Assert.NotNull(detail);
        PollRosterEntry rosterRow = detail!.Roster.Single(r => r.Participant.UserId == blockedVoter.Id);
        PollOptionGridEntry option = detail.Options.Single(o => o.Id == optionId);
        PollRosterEntry optionRow = Assert.Single(option.AvailableVoters);

        Assert.True(rosterRow.MaskedForViewer);
        Assert.True(optionRow.MaskedForViewer);
        Assert.Null(rosterRow.DisplayName);
        Assert.Null(optionRow.DisplayName);
        Assert.Null(rosterRow.Avatar);
        Assert.Null(optionRow.Avatar);
        Assert.Equal(rosterRow.ParticipationId, optionRow.ParticipationId);
        Assert.Same(rosterRow, optionRow);
    }

    [Fact]
    public async Task GetPollDetail_OptionVoterList_OrdersByFirstSubmittedResponseAscending()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        User first = FriendTestData.Active($"f{Guid.NewGuid():N}"[..12], "First");
        User second = FriendTestData.Active($"s{Guid.NewGuid():N}"[..12], "Second");
        User third = FriendTestData.Active($"t{Guid.NewGuid():N}"[..12], "Third");
        reads.Nameplates[organizer.Id.Value] = new UserNameplate(organizer.DisplayName, organizer.Avatar);
        reads.Nameplates[first.Id.Value] = new UserNameplate(first.DisplayName, first.Avatar);
        reads.Nameplates[second.Id.Value] = new UserNameplate(second.DisplayName, second.Avatar);
        reads.Nameplates[third.Id.Value] = new UserNameplate(third.DisplayName, third.Avatar);

        Poll poll = NewPoll(organizer.Id, [first.Id, second.Id, third.Id], clock);
        PollOptionId optionId = poll.Options[0].Id;

        // Vote out of "arrival" order so a naive participant/roster ordering would fail this assertion.
        poll.Vote(ParticipantRef.ForUser(third.Id), optionId, VoteChoice.Available, clock);
        clock.Advance(Duration.FromMinutes(1));
        poll.Vote(ParticipantRef.ForUser(first.Id), optionId, VoteChoice.Available, clock);
        clock.Advance(Duration.FromMinutes(1));
        poll.Vote(ParticipantRef.ForUser(second.Id), optionId, VoteChoice.Available, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, organizer.Id, default);

        Assert.NotNull(detail);
        PollOptionGridEntry option = detail!.Options.Single(o => o.Id == optionId);
        Assert.Equal(
            [third.Id, first.Id, second.Id],
            option.AvailableVoters.Select(v => v.Participant.UserId!.Value));
    }

    [Fact]
    public async Task GetPollDetail_OptionVoterList_ExcludesNonVoter()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        User voter = FriendTestData.Active($"v{Guid.NewGuid():N}"[..12], "Voter");
        User silent = FriendTestData.Active($"s{Guid.NewGuid():N}"[..12], "Silent");
        reads.Nameplates[organizer.Id.Value] = new UserNameplate(organizer.DisplayName, organizer.Avatar);
        reads.Nameplates[voter.Id.Value] = new UserNameplate(voter.DisplayName, voter.Avatar);
        reads.Nameplates[silent.Id.Value] = new UserNameplate(silent.DisplayName, silent.Avatar);

        Poll poll = NewPoll(organizer.Id, [voter.Id, silent.Id], clock);
        PollOptionId optionId = poll.Options[0].Id;
        poll.Vote(ParticipantRef.ForUser(voter.Id), optionId, VoteChoice.Available, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, organizer.Id, default);

        Assert.NotNull(detail);
        PollOptionGridEntry option = detail!.Options.Single(o => o.Id == optionId);
        Assert.DoesNotContain(option.AvailableVoters, v => v.Participant.UserId == silent.Id);
        Assert.DoesNotContain(option.MaybeVoters, v => v.Participant.UserId == silent.Id);
        Assert.DoesNotContain(option.UnavailableVoters, v => v.Participant.UserId == silent.Id);
    }

    [Fact]
    public async Task GetPollDetail_OptionVoterList_IncludesGuestVoter()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        reads.Nameplates[organizer.Id.Value] = new UserNameplate(organizer.DisplayName, organizer.Avatar);

        Poll poll = NewPrivatePoll(organizer.Id, clock);
        (GuestLink link, _) = poll.CreateGuestLink(clock);
        GuestIdentity guest = poll.ClaimGuestLink(link.Id, DisplayName.Create("Guesty"), clock);

        PollOptionId optionId = poll.Options[0].Id;
        poll.Vote(ParticipantRef.ForGuest(guest.Id), optionId, VoteChoice.Maybe, clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, organizer.Id, default);

        Assert.NotNull(detail);
        PollOptionGridEntry option = detail!.Options.Single(o => o.Id == optionId);
        PollRosterEntry guestVoter = Assert.Single(option.MaybeVoters);
        Assert.Equal(guest.Id, guestVoter.Participant.GuestIdentityId);
        Assert.Equal("Guesty", guestVoter.DisplayName?.Value);
        Assert.False(guestVoter.MaskedForViewer);
    }

    /// <summary>D-196: the initial poll-creation options are organizer-suggested, so they carry no attribution.</summary>
    [Fact]
    public async Task GetPollDetail_OptionAttribution_NullForOrganizerCreatedOption_D196()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        User invitee = FriendTestData.Active($"i{Guid.NewGuid():N}"[..12], "Invitee");
        reads.Nameplates[organizer.Id.Value] = new UserNameplate(organizer.DisplayName, organizer.Avatar);
        reads.Nameplates[invitee.Id.Value] = new UserNameplate(invitee.DisplayName, invitee.Avatar);

        Poll poll = NewPoll(organizer.Id, [invitee.Id], clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, organizer.Id, default);

        Assert.NotNull(detail);
        Assert.All(detail!.Options, option => Assert.Null(option.SuggestedByEntry));
    }

    /// <summary>D-196: a non-organizer suggestion resolves attribution through the same roster row the voter lists use.</summary>
    [Fact]
    public async Task GetPollDetail_OptionAttribution_PresentForNonOrganizerSuggester_D196()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User organizer = FriendTestData.Active($"o{Guid.NewGuid():N}"[..12], "Organizer");
        User invitee = FriendTestData.Active($"i{Guid.NewGuid():N}"[..12], "Invitee");
        reads.Nameplates[organizer.Id.Value] = new UserNameplate(organizer.DisplayName, organizer.Avatar);
        reads.Nameplates[invitee.Id.Value] = new UserNameplate(invitee.DisplayName, invitee.Avatar);

        Poll poll = NewPoll(organizer.Id, [invitee.Id], clock);
        PollOption suggested = poll.SuggestOption(
            ParticipantRef.ForUser(invitee.Id), clock.GetCurrentInstant() + Duration.FromHours(7), Duration15.FromMinutes(PollSpecs.DefaultDurationMinutes), clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, organizer.Id, default);

        Assert.NotNull(detail);
        PollOptionGridEntry option = detail!.Options.Single(o => o.Id == suggested.Id);
        Assert.NotNull(option.SuggestedByEntry);
        Assert.Equal(invitee.Id, option.SuggestedByEntry!.Participant.UserId);
        Assert.Equal("Invitee", option.SuggestedByEntry.DisplayName?.Value);
        Assert.False(option.SuggestedByEntry.MaskedForViewer);
    }

    /// <summary>D-196: a blocked-pair suggester's attribution masks identically to their roster/voter-list row.</summary>
    [Fact]
    public async Task GetPollDetail_OptionAttribution_MaskedForBlockedPairSuggester_D196()
    {
        FakePollRepository polls = new();
        FakePollReadRepository reads = new();
        FakeFriendshipRepository friendships = new();
        FakeBlockRepository blocks = new();
        FakeNotificationMuteRepository mutes = new();
        MutableClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));

        User viewer = FriendTestData.Active($"v{Guid.NewGuid():N}"[..12], "Viewer");
        User blockedSuggester = FriendTestData.Active($"x{Guid.NewGuid():N}"[..12], "Blocked Suggester");
        reads.Nameplates[viewer.Id.Value] = new UserNameplate(viewer.DisplayName, viewer.Avatar);
        reads.Nameplates[blockedSuggester.Id.Value] = new UserNameplate(blockedSuggester.DisplayName, blockedSuggester.Avatar);

        blocks.Items.Add(Block.Create(viewer.Id, blockedSuggester.Id, blockedSuggester.Username!.Value, clock));

        Poll poll = NewPoll(viewer.Id, [blockedSuggester.Id], clock);
        PollOption suggested = poll.SuggestOption(
            ParticipantRef.ForUser(blockedSuggester.Id), clock.GetCurrentInstant() + Duration.FromHours(7), Duration15.FromMinutes(PollSpecs.DefaultDurationMinutes), clock);
        _ = poll.DequeueEvents();
        polls.Add(poll);

        var useCase = new PollReadUseCases(polls, reads, friendships, blocks, new FakeFriendGroupRepository(), mutes);

        PollDetail? detail = await useCase.GetPollDetailAsync(poll.Id, viewer.Id, default);

        Assert.NotNull(detail);
        PollOptionGridEntry option = detail!.Options.Single(o => o.Id == suggested.Id);
        Assert.NotNull(option.SuggestedByEntry);
        Assert.True(option.SuggestedByEntry!.MaskedForViewer);
        Assert.Null(option.SuggestedByEntry.DisplayName);
    }

    private static Poll NewPoll(UserId organizerId, IReadOnlyList<UserId> inviteeIds, MutableClock clock) =>
        Poll.Create(
            organizerId,
            new PollDetails(
                SessionTitle.Create("Raid poll"),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                visibility: ContentVisibility.Public),
            PollDeadlineChoice.Hours24,
            PollSpecs.Of(clock.GetCurrentInstant() + Duration.FromHours(5), clock.GetCurrentInstant() + Duration.FromHours(6)),
            inviteeIds,
            clock);

    private static Poll NewPrivatePoll(UserId organizerId, MutableClock clock) =>
        Poll.Create(
            organizerId,
            new PollDetails(
                SessionTitle.Create("Raid poll"),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                visibility: ContentVisibility.Private),
            PollDeadlineChoice.Hours24,
            PollSpecs.Of(clock.GetCurrentInstant() + Duration.FromHours(5), clock.GetCurrentInstant() + Duration.FromHours(6)),
            [],
            clock);
}

// ---- shared in-memory test doubles for this file (FakePollRepository/FakeFriendshipRepository/
// FakeBlockRepository/MutableClock are already declared, internal to this namespace, by
// PollCommandTransactionTests.cs -- reused here rather than redeclared) --------------------------

internal sealed class FakePollReadRepository : IPollReadRepository
{
    internal Dictionary<Guid, UserNameplate> Nameplates { get; } = [];

    public Task<IReadOnlyDictionary<Guid, UserNameplate>> FindNameplatesAsync(
        IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, UserNameplate>>(Nameplates);

    public Task<SessionPage<PollSummary>> ListInvolvedAsync(
        UserId viewerId, SessionListCursor? cursor, int pageSize, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This test double never exercises the list bands.");

    public Task<SessionPage<PollSummary>> ListDiscoverableAsync(
        UserId viewerId, SessionListCursor? cursor, int pageSize, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This test double never exercises the list bands.");

    public Task<IReadOnlyList<CalendarPollOption>> ListCalendarPollOptionsAsync(
        UserId viewerId, TimeRange range, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This test double never exercises the calendar layer.");
}

internal sealed class FakeNotificationMuteRepository : INotificationMuteRepository
{
    public Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AddAsync(NotificationMute mute, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveForTargetAsync(UserId userId, SessionId? sessionId, PollId? pollId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> ExistsForTargetAsync(UserId userId, SessionId? sessionId, PollId? pollId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
