using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

public class AvailabilityVisibilityTests
{
    [Fact]
    public void CanRead_GrantsTheOwnerTheirOwnBlocks_D009()
    {
        UserId owner = UserId.New();

        Assert.True(AvailabilityVisibility.CanRead(owner, TestViewers.Of(owner)));
    }

    [Fact]
    public void CanRead_GrantsAnAcceptedFriend_D024()
    {
        UserId owner = UserId.New();

        Assert.True(AvailabilityVisibility.CanRead(owner, TestViewers.FriendOf(owner)));
    }

    [Fact]
    public void CanRead_RefusesEveryoneElse_D024()
    {
        UserId owner = UserId.New();

        Assert.False(AvailabilityVisibility.CanRead(owner, TestViewers.Stranger()));
    }

    [Fact]
    public void CanRead_RefusesABlockedCounterpart_D065()
    {
        UserId owner = UserId.New();
        Viewer stale = TestViewers.Of(UserId.New(), friends: [owner], blocked: [owner]);

        Assert.False(AvailabilityVisibility.CanRead(owner, stale));
    }

    [Fact]
    public void CanWrite_IsTheOwnersAlone_D009()
    {
        UserId owner = UserId.New();

        Assert.True(AvailabilityVisibility.CanWrite(owner, TestViewers.Of(owner)));
        Assert.False(AvailabilityVisibility.CanWrite(owner, TestViewers.FriendOf(owner)));
        Assert.False(AvailabilityVisibility.CanWrite(owner, TestViewers.Stranger()));
    }

    [Fact]
    public void ReadableOwners_KeepsFriendsAndTheViewerAndDropsTheRest_D024()
    {
        UserId viewerId = UserId.New();
        UserId friend = UserId.New();
        UserId stranger = UserId.New();
        Viewer viewer = TestViewers.Of(viewerId, friends: [friend]);

        IReadOnlyList<UserId> readable =
            AvailabilityVisibility.ReadableOwners([friend, stranger, viewerId, friend], viewer);

        Assert.Equal(2, readable.Count);
        Assert.Contains(friend, readable);
        Assert.Contains(viewerId, readable);
        Assert.DoesNotContain(stranger, readable);
    }

    [Fact]
    public void RequireRead_FailsClosedWithNotFound_D024()
    {
        UserId owner = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => AvailabilityVisibility.RequireRead(owner, TestViewers.Stranger()));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void RequireWrite_FailsClosedForAFriend_D009()
    {
        UserId owner = UserId.New();

        DomainException error = Assert.Throws<DomainException>(
            () => AvailabilityVisibility.RequireWrite(owner, TestViewers.FriendOf(owner)));

        Assert.Equal(DomainError.NotFound, error.Error);
    }

    [Fact]
    public void RequireRead_PassesForTheOwnerAndTheirFriends_D024()
    {
        UserId owner = UserId.New();

        AvailabilityVisibility.RequireRead(owner, TestViewers.Of(owner));
        AvailabilityVisibility.RequireRead(owner, TestViewers.FriendOf(owner));
        AvailabilityVisibility.RequireWrite(owner, TestViewers.Of(owner));
    }

    [Fact]
    public void CanRead_RejectsANullViewer_D024()
    {
        UserId owner = UserId.New();

        Assert.Throws<ArgumentNullException>(() => AvailabilityVisibility.CanRead(owner, null!));
        Assert.Throws<ArgumentNullException>(() => AvailabilityVisibility.CanWrite(owner, null!));
        Assert.Throws<ArgumentNullException>(() => AvailabilityVisibility.ReadableOwners(null!, TestViewers.Stranger()));
    }
}
