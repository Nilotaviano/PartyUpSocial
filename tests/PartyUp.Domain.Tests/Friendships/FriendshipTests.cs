using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Friendships;

public class FriendshipTests
{
    [Fact]
    public void Request_StoresThePairCanonicallyAndStartsPending_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User requester = TestUsers.Active(clock, "one");
        User addressee = TestUsers.Active(clock, "two");

        Friendship friendship = Friendship.Request(requester, addressee, clock);

        Assert.True(friendship.IsPending);
        Assert.False(friendship.IsAccepted);
        Assert.Equal(requester.Id, friendship.RequestedBy);
        Assert.Equal(addressee.Id, friendship.AddresseeId);
        Assert.True(friendship.UserLoId.Value.CompareTo(friendship.UserHiId.Value) < 0);
        Assert.Equal(TestUsers.Origin, friendship.RequestedAt);
        Assert.Null(friendship.AcceptedAt);
    }

    [Fact]
    public void Request_ProducesTheSamePairWhicheverSideAsks_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User one = TestUsers.Active(clock, "one");
        User two = TestUsers.Active(clock, "two");

        Friendship forwards = Friendship.Request(one, two, clock);
        Friendship backwards = Friendship.Request(two, one, clock);

        Assert.Equal(forwards.UserLoId, backwards.UserLoId);
        Assert.Equal(forwards.UserHiId, backwards.UserHiId);
        Assert.NotEqual(forwards.RequestedBy, backwards.RequestedBy);
    }

    [Fact]
    public void Request_NotifiesTheTargetUser_D105()
    {
        FakeClock clock = TestUsers.Clock();
        User requester = TestUsers.Active(clock, "one");
        User addressee = TestUsers.Active(clock, "two");

        Friendship friendship = Friendship.Request(requester, addressee, clock);

        FriendRequestReceived raised = Assert.Single(friendship.DequeueEvents().OfType<FriendRequestReceived>());
        Assert.Equal(friendship.Id, raised.FriendshipId);
        Assert.Equal(requester.Id, raised.RequestedBy);
        Assert.Equal(addressee.Id, raised.AddresseeId);
    }

    [Fact]
    public void Request_RejectsSelfFriending_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        DomainException error = Assert.Throws<DomainException>(() => Friendship.Request(user, user, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Request_RejectsAnOnboardingCounterpartInEitherRole_D127()
    {
        FakeClock clock = TestUsers.Clock();
        User active = TestUsers.Active(clock, "one");
        User onboarding = TestUsers.Onboarding(clock, "PU-XYZ789");

        Assert.Equal(
            DomainError.Forbidden,
            Assert.Throws<DomainException>(() => Friendship.Request(active, onboarding, clock)).Error);
        Assert.Equal(
            DomainError.Forbidden,
            Assert.Throws<DomainException>(() => Friendship.Request(onboarding, active, clock)).Error);
    }

    [Fact]
    public void Request_RejectsATombstonedCounterpartInEitherRole_D092()
    {
        FakeClock clock = TestUsers.Clock();
        User tombstoned = TestUsers.Tombstoned(clock, "gone");
        User active = TestUsers.Active(clock, "one");

        Assert.Equal(
            DomainError.Forbidden,
            Assert.Throws<DomainException>(() => Friendship.Request(active, tombstoned, clock)).Error);
        Assert.Equal(
            DomainError.Forbidden,
            Assert.Throws<DomainException>(() => Friendship.Request(tombstoned, active, clock)).Error);
    }

    [Fact]
    public void Request_StillWorksDuringTheDeletionGracePeriod_D148()
    {
        FakeClock clock = TestUsers.Clock();
        User pending = TestUsers.PendingDeletion(clock, "leaving");
        User active = TestUsers.Active(clock, "one");

        Friendship friendship = Friendship.Request(active, pending, clock);

        Assert.True(friendship.IsPending);
    }

    [Fact]
    public void Request_RejectsNullArguments_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Throws<ArgumentNullException>(() => Friendship.Request(null!, user, clock));
        Assert.Throws<ArgumentNullException>(() => Friendship.Request(user, null!, clock));
        Assert.Throws<ArgumentNullException>(() => Friendship.Request(user, TestUsers.Active(clock, "two"), null!));
    }

    [Fact]
    public void Accept_ByTheAddressee_MakesTheConnectionMutual_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User requester = TestUsers.Active(clock, "one");
        User addressee = TestUsers.Active(clock, "two");
        Friendship friendship = Friendship.Request(requester, addressee, clock);
        friendship.DequeueEvents();

        clock.Advance(Duration.FromMinutes(5));
        friendship.Accept(addressee.Id, clock);

        Assert.True(friendship.IsAccepted);
        Assert.Equal(FriendshipStatus.Accepted, friendship.Status);
        Assert.Equal(TestUsers.Origin + Duration.FromMinutes(5), friendship.AcceptedAt);

        FriendshipAccepted raised = Assert.Single(friendship.DequeueEvents().OfType<FriendshipAccepted>());
        Assert.Equal(friendship.Id, raised.FriendshipId);
        Assert.Equal(friendship.UserLoId, raised.UserLoId);
        Assert.Equal(friendship.UserHiId, raised.UserHiId);
    }

    [Fact]
    public void Accept_ByTheRequesterIsRejectedBecauseTheConnectionIsMutual_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User requester = TestUsers.Active(clock, "one");
        User addressee = TestUsers.Active(clock, "two");
        Friendship friendship = Friendship.Request(requester, addressee, clock);

        DomainException error = Assert.Throws<DomainException>(() => friendship.Accept(requester.Id, clock));

        Assert.Equal(DomainError.Forbidden, error.Error);
        Assert.True(friendship.IsPending);
    }

    [Fact]
    public void Accept_ByAThirdPartyIsRejected_D015()
    {
        FakeClock clock = TestUsers.Clock();
        Friendship friendship = Friendship.Request(
            TestUsers.Active(clock, "one"),
            TestUsers.Active(clock, "two"),
            clock);

        DomainException error = Assert.Throws<DomainException>(() => friendship.Accept(UserId.New(), clock));

        Assert.Equal(DomainError.Forbidden, error.Error);
        Assert.True(friendship.IsPending);
    }

    [Fact]
    public void Accept_ASecondTimeIsRejected_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User requester = TestUsers.Active(clock, "one");
        User addressee = TestUsers.Active(clock, "two");
        Friendship friendship = Friendship.Request(requester, addressee, clock);
        friendship.Accept(addressee.Id, clock);

        DomainException error = Assert.Throws<DomainException>(() => friendship.Accept(addressee.Id, clock));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    [Fact]
    public void CanonicalPair_OrdersDeterministicallyAndRejectsIdenticalIds_D015()
    {
        UserId first = UserId.New();
        UserId second = UserId.New();

        (UserId lo, UserId hi) = Friendship.CanonicalPair(first, second);
        (UserId reversedLo, UserId reversedHi) = Friendship.CanonicalPair(second, first);

        Assert.Equal(lo, reversedLo);
        Assert.Equal(hi, reversedHi);
        Assert.True(lo.Value.CompareTo(hi.Value) < 0);
        Assert.Throws<DomainException>(() => Friendship.CanonicalPair(first, first));
    }

    [Fact]
    public void InvolvesAndCounterpart_AnswerForBothSidesOnly_D015()
    {
        FakeClock clock = TestUsers.Clock();
        User requester = TestUsers.Active(clock, "one");
        User addressee = TestUsers.Active(clock, "two");
        Friendship friendship = Friendship.Request(requester, addressee, clock);
        UserId stranger = UserId.New();

        Assert.True(friendship.Involves(requester.Id));
        Assert.True(friendship.Involves(addressee.Id));
        Assert.False(friendship.Involves(stranger));
        Assert.Equal(addressee.Id, friendship.Counterpart(requester.Id));
        Assert.Equal(requester.Id, friendship.Counterpart(addressee.Id));
        Assert.True(friendship.IsBetween(addressee.Id, requester.Id));
        Assert.False(friendship.IsBetween(requester.Id, stranger));

        Assert.Equal(
            DomainError.NotFound,
            Assert.Throws<DomainException>(() => friendship.Counterpart(stranger)).Error);
    }
}
