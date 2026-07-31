using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Polls;
using PartyUp.Domain.Tests.Sessions;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class IdentityMaskingTests
{
    [Fact]
    public void ForUser_RendersDisplayNameAndAvatarToAnyViewerWithAccess_D030()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        MaskedIdentity rendered = IdentityMasking.ForUser(subject, TestViewers.Stranger());

        Assert.False(rendered.IsAnonymous);
        Assert.Equal(MaskingReason.None, rendered.Reason);
        Assert.Equal(DisplayName.Create("Alex"), rendered.DisplayName);
        Assert.Equal(subject.Avatar, rendered.Avatar);
    }

    [Fact]
    public void ForUser_RendersTheSameForFriendsAndNonFriends_D114()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        MaskedIdentity toFriend = IdentityMasking.ForUser(subject, TestViewers.FriendOf(subject.Id));
        MaskedIdentity toStranger = IdentityMasking.ForUser(subject, TestViewers.Stranger());

        Assert.Equal(toFriend, toStranger);
    }

    [Fact]
    public void ForUser_RendersATombstoneAnonymouslyToEveryViewer_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Tombstoned(clock, "alex");

        MaskedIdentity toStranger = IdentityMasking.ForUser(subject, TestViewers.Stranger());
        MaskedIdentity toFriend = IdentityMasking.ForUser(subject, TestViewers.FriendOf(subject.Id));

        Assert.True(toStranger.IsAnonymous);
        Assert.Equal(MaskingReason.Tombstoned, toStranger.Reason);
        Assert.Null(toStranger.DisplayName);
        Assert.Null(toStranger.Avatar);
        Assert.True(toFriend.IsAnonymous);
    }

    [Fact]
    public void ForUser_MasksOnlyBetweenTheBlockedPair_D076()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        MaskedIdentity toCounterpart = IdentityMasking.ForUser(subject, TestViewers.Of(UserId.New(), blocked: [subject.Id]));
        MaskedIdentity toThirdParty = IdentityMasking.ForUser(subject, TestViewers.Stranger());

        Assert.True(toCounterpart.IsAnonymous);
        Assert.Equal(MaskingReason.Blocked, toCounterpart.Reason);
        Assert.False(toThirdParty.IsAnonymous);
    }

    [Fact]
    public void ForUser_PutsTheTombstoneRuleAheadOfTheBlockRule_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Tombstoned(clock, "alex");

        MaskedIdentity rendered = IdentityMasking.ForUser(subject, TestViewers.Of(UserId.New(), blocked: [subject.Id]));

        Assert.Equal(MaskingReason.Tombstoned, rendered.Reason);
    }

    [Fact]
    public void ForUser_LeavesAPendingDeletionUserUnmasked_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.PendingDeletion(clock, "alex");

        MaskedIdentity rendered = IdentityMasking.ForUser(subject, TestViewers.Stranger());

        Assert.False(rendered.IsAnonymous);
    }

    [Fact]
    public void ForUser_FailsClosedOnAnOnboardingProfileWithNoDisplayName_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Onboarding(clock);

        MaskedIdentity rendered = IdentityMasking.ForUser(subject, TestViewers.Stranger());

        Assert.True(rendered.IsAnonymous);
        Assert.Equal(MaskingReason.ProfileIncomplete, rendered.Reason);
    }

    [Fact]
    public void ForGuestIdentity_RendersTheSelfSuppliedNameAsIs_D150()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        (GuestLink link, _) = s.Session.CreateGuestLink(clock);
        GuestIdentity guest = s.Session.ClaimGuestLink(
            link.Id,
            DisplayName.Create("Casey"),
            ParticipationStatus.Going,
            clock);

        MaskedIdentity rendered = IdentityMasking.ForGuestIdentity(guest);

        Assert.False(rendered.IsAnonymous);
        Assert.Equal(DisplayName.Create("Casey"), rendered.DisplayName);
        Assert.Null(rendered.Avatar);
    }

    [Fact]
    public void ForUserSeenByGuest_MatchesWhatAParticipantSees_D030()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock, "alex", "Alex");

        Assert.Equal(
            IdentityMasking.ForUser(subject, TestViewers.Stranger()),
            IdentityMasking.ForUserSeenByGuest(subject));
    }

    [Fact]
    public void ForUserSeenByGuest_StillAnonymisesATombstone_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Tombstoned(clock, "alex");

        Assert.Equal(MaskingReason.Tombstoned, IdentityMasking.ForUserSeenByGuest(subject).Reason);
    }

    [Fact]
    public void Roster_MasksOnlyTheBlockedCounterpartAndLeavesCapacityAlone_D086()
    {
        FakeClock userClock = TestUsers.Clock();
        User organizer = TestUsers.Active(userClock, "alex", "Alex");
        User first = TestUsers.Active(userClock, "bee", "Bee");
        User second = TestUsers.Active(userClock, "cee", "Cee");
        Dictionary<UserId, User> directory = new()
        {
            [organizer.Id] = organizer,
            [first.Id] = first,
            [second.Id] = second,
        };

        FakeClock clock = TestSessions.Clock();
        Session session = Session.Create(organizer.Id, TestSessions.Details(), [first.Id, second.Id], clock);
        session.Rsvp(ParticipantRef.ForUser(first.Id), ParticipationStatus.Going, clock);
        session.Rsvp(ParticipantRef.ForUser(second.Id), ParticipationStatus.Going, clock);
        (GuestLink link, _) = session.CreateGuestLink(clock);
        GuestIdentity guest = session.ClaimGuestLink(
            link.Id,
            DisplayName.Create("Casey"),
            ParticipationStatus.Going,
            clock);

        Viewer viewer = TestViewers.Of(first.Id, blocked: [second.Id]);
        List<MaskedIdentity> roster = [];
        foreach (Participation row in session.Participations)
        {
            roster.Add(row.UserId is { } userId
                ? IdentityMasking.ForUser(directory[userId], viewer)
                : IdentityMasking.ForGuestIdentity(guest));
        }

        Assert.Equal(4, roster.Count);
        Assert.Equal(DisplayName.Create("Alex"), roster[0].DisplayName);
        Assert.Equal(DisplayName.Create("Bee"), roster[1].DisplayName);
        Assert.Equal(MaskingReason.Blocked, roster[2].Reason);
        Assert.Equal(DisplayName.Create("Casey"), roster[3].DisplayName);
        Assert.Equal(4, session.ActiveGoingCount);
    }

    [Fact]
    public void VoterGrid_MasksTheBlockedCounterpartWhileTheirVoteStillCounts_D086()
    {
        FakeClock userClock = TestUsers.Clock();
        User organizer = TestUsers.Active(userClock, "alex", "Alex");
        User first = TestUsers.Active(userClock, "bee", "Bee");
        User second = TestUsers.Active(userClock, "cee", "Cee");

        FakeClock clock = TestPolls.Clock();
        Poll poll = Poll.Create(
            organizer.Id,
            TestPolls.Details(),
            PollDeadlineChoice.Hours24,
            [TestPolls.Hours(72), TestPolls.Hours(96)],
            [first.Id, second.Id],
            clock);
        poll.Vote(ParticipantRef.ForUser(second.Id), poll.Options[0].Id, VoteChoice.Available, clock);

        Viewer viewer = TestViewers.Of(first.Id, blocked: [second.Id]);

        Assert.Equal(MaskingReason.Blocked, IdentityMasking.ForUser(second, viewer).Reason);
        Assert.False(IdentityMasking.ForUser(organizer, viewer).IsAnonymous);
        Assert.Equal(VoteChoice.Available, Assert.Single(poll.Votes).Choice);
        Assert.Equal(3, poll.ActiveParticipantCount);
    }

    [Fact]
    public void OptionAttribution_RendersAGuestSuggesterAsIs_D150()
    {
        FakeClock clock = TestPolls.Clock();
        PollUnderTest p = TestPolls.New(clock, inviteeCount: 1);
        (_, ParticipantRef guest) = PollGuests.Claim(p.Poll, clock, "Casey");
        PollOption suggested = p.Poll.SuggestOption(guest, TestPolls.Hours(120), clock);

        GuestIdentity identity = p.Poll.GuestIdentities.Single(g => g.Id == suggested.SuggestedByGuestIdentityId);
        MaskedIdentity attribution = IdentityMasking.ForGuestIdentity(identity);

        Assert.True(suggested.WasSuggestedBy(guest));
        Assert.Equal(DisplayName.Create("Casey"), attribution.DisplayName);
    }

    [Fact]
    public void Anonymous_RejectsANonMaskingReason_D076()
    {
        DomainException error = Assert.Throws<DomainException>(() => MaskedIdentity.Anonymous(MaskingReason.None));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void ForUser_RejectsNullArguments_D076()
    {
        FakeClock clock = TestUsers.Clock();
        User subject = TestUsers.Active(clock);

        Assert.Throws<ArgumentNullException>(() => IdentityMasking.ForUser(null!, TestViewers.Stranger()));
        Assert.Throws<ArgumentNullException>(() => IdentityMasking.ForUser(subject, null!));
        Assert.Throws<ArgumentNullException>(() => IdentityMasking.ForUserSeenByGuest(null!));
        Assert.Throws<ArgumentNullException>(() => IdentityMasking.ForGuestIdentity(null!));
    }
}
