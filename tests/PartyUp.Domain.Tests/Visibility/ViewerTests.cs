using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class ViewerTests
{
    [Fact]
    public void From_KeepsOnlyAcceptedFriendshipsAsExtenderPaths_D018()
    {
        FakeClock clock = TestUsers.Clock();
        User viewer = TestUsers.Active(clock, "alex", "Alex");
        User accepted = TestUsers.Active(clock, "bee", "Bee");
        User pending = TestUsers.Active(clock, "cee", "Cee");

        Friendship acceptedFriendship = Friendship.Request(viewer, accepted, clock);
        acceptedFriendship.Accept(accepted.Id, clock);
        Friendship pendingFriendship = Friendship.Request(viewer, pending, clock);

        Viewer built = Viewer.From(viewer.Id, [acceptedFriendship, pendingFriendship], []);

        Assert.True(built.IsFriendOf(accepted.Id));
        Assert.False(built.IsFriendOf(pending.Id));
        Assert.Equal(accepted.Id, Assert.Single(built.AcceptedFriendIds));
    }

    [Fact]
    public void From_IgnoresFriendshipsAndBlocksThatDoNotInvolveTheViewer_D065()
    {
        FakeClock clock = TestUsers.Clock();
        User viewer = TestUsers.Active(clock, "alex", "Alex");
        User first = TestUsers.Active(clock, "bee", "Bee");
        User second = TestUsers.Active(clock, "cee", "Cee");

        Friendship others = Friendship.Request(first, second, clock);
        others.Accept(second.Id, clock);
        Block othersBlock = Block.Create(first.Id, second.Id, clock);

        Viewer built = Viewer.From(viewer.Id, [others], [othersBlock]);

        Assert.Empty(built.AcceptedFriendIds);
        Assert.Empty(built.BlockedCounterpartIds);
    }

    [Fact]
    public void From_TreatsABlockAsMutualWhicheverSideRecordedIt_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId viewerId = UserId.New();
        UserId counterpart = UserId.New();

        Viewer asBlocker = Viewer.From(viewerId, [], [Block.Create(viewerId, counterpart, clock)]);
        Viewer asBlocked = Viewer.From(viewerId, [], [Block.Create(counterpart, viewerId, clock)]);

        Assert.True(asBlocker.IsBlockedWith(counterpart));
        Assert.True(asBlocked.IsBlockedWith(counterpart));
    }

    [Fact]
    public void Create_DefaultsToNoRelationsAtAll_D019()
    {
        Viewer viewer = Viewer.Create(UserId.New());

        Assert.Empty(viewer.AcceptedFriendIds);
        Assert.Empty(viewer.BlockedCounterpartIds);
    }

    [Fact]
    public void Create_RefusesToMakeAViewerTheirOwnFriend_D009()
    {
        UserId id = UserId.New();

        DomainException error = Assert.Throws<DomainException>(() => Viewer.Create(id, [id]));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RefusesToMakeAViewerTheirOwnBlockedCounterpart_D065()
    {
        UserId id = UserId.New();

        DomainException error = Assert.Throws<DomainException>(() => Viewer.Create(id, null, [id]));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_DeduplicatesRepeatedRelations_D018()
    {
        UserId friend = UserId.New();

        Viewer viewer = Viewer.Create(UserId.New(), [friend, friend]);

        Assert.Equal(friend, Assert.Single(viewer.AcceptedFriendIds));
    }

    [Fact]
    public void From_RejectsNullCollections_D018()
    {
        UserId id = UserId.New();

        Assert.Throws<ArgumentNullException>(() => Viewer.From(id, null!, []));
        Assert.Throws<ArgumentNullException>(() => Viewer.From(id, [], null!));
    }
}
