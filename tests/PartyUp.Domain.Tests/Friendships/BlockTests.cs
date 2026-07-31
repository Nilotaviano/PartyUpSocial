using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Tests.Users;

namespace PartyUp.Domain.Tests.Friendships;

public class BlockTests
{
    [Fact]
    public void Create_StoresTheOrderedPairWithItsInstant_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId blocker = UserId.New();
        UserId blocked = UserId.New();

        Block block = Block.Create(blocker, blocked, clock);

        Assert.Equal(blocker, block.BlockerId);
        Assert.Equal(blocked, block.BlockedId);
        Assert.Equal(TestUsers.Origin, block.CreatedAt);
        Assert.NotEqual(default, block.Id.Value);
    }

    [Fact]
    public void Create_KeepsTheTwoDirectionsDistinctRows_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId one = UserId.New();
        UserId two = UserId.New();

        Block forwards = Block.Create(one, two, clock);
        Block backwards = Block.Create(two, one, clock);

        Assert.NotEqual(forwards.Id, backwards.Id);
        Assert.Equal(forwards.BlockerId, backwards.BlockedId);
        Assert.Equal(forwards.BlockedId, backwards.BlockerId);
    }

    [Fact]
    public void Create_RejectsBlockingYourself_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId user = UserId.New();

        DomainException error = Assert.Throws<DomainException>(() => Block.Create(user, user, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsANullClock_D065()
    {
        Assert.Throws<ArgumentNullException>(() => Block.Create(UserId.New(), UserId.New(), null!));
    }

    [Fact]
    public void Create_RaisesUserBlockedForTheLaterCrossAggregateEffects_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId blocker = UserId.New();
        UserId blocked = UserId.New();

        Block block = Block.Create(blocker, blocked, clock);

        UserBlocked raised = Assert.Single(block.DequeueEvents().OfType<UserBlocked>());
        Assert.Equal(block.Id, raised.BlockId);
        Assert.Equal(blocker, raised.BlockerId);
        Assert.Equal(blocked, raised.BlockedId);
    }

    [Fact]
    public void Hides_IsMutualRegardlessOfWhoBlockedWhom_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId blocker = UserId.New();
        UserId blocked = UserId.New();
        Block block = Block.Create(blocker, blocked, clock);

        Assert.True(block.Hides(blocker, blocked));
        Assert.True(block.Hides(blocked, blocker));
    }

    [Fact]
    public void Hides_IgnoresThirdPartiesAndSelfPairs_D065()
    {
        FakeClock clock = TestUsers.Clock();
        UserId blocker = UserId.New();
        UserId blocked = UserId.New();
        UserId stranger = UserId.New();
        Block block = Block.Create(blocker, blocked, clock);

        Assert.False(block.Hides(blocker, stranger));
        Assert.False(block.Hides(stranger, blocked));
        Assert.False(block.Hides(blocker, blocker));
        Assert.True(block.Involves(blocked));
        Assert.False(block.Involves(stranger));
    }
}
