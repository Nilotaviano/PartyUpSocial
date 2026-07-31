using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class TypedIdTests
{
    [Fact]
    public void New_ProducesDistinctVersion7Guids_ForEveryTypedId()
    {
        AssertIdContract(UserId.New);
        AssertIdContract(FriendshipId.New);
        AssertIdContract(BlockId.New);
        AssertIdContract(PlatformLinkId.New);
        AssertIdContract(SessionId.New);
        AssertIdContract(ParticipationId.New);
        AssertIdContract(JoinRequestId.New);
        AssertIdContract(GuestIdentityId.New);
        AssertIdContract(GuestLinkId.New);
        AssertIdContract(PollId.New);
        AssertIdContract(PollOptionId.New);
        AssertIdContract(PollParticipantId.New);
        AssertIdContract(AvailabilitySeriesId.New);
        AssertIdContract(AvailabilityBlockId.New);
        AssertIdContract(NotificationId.New);
        AssertIdContract(NotificationMuteId.New);
        AssertIdContract(PushSubscriptionId.New);
    }

    [Fact]
    public void From_RoundTripsTheUnderlyingGuid()
    {
        Guid raw = Guid.CreateVersion7();

        Assert.Equal(raw, UserId.From(raw).Value);
        Assert.Equal(UserId.From(raw), UserId.From(raw));
    }

    [Fact]
    public void DifferentIdTypes_WrappingTheSameGuid_AreNotEqual()
    {
        Guid raw = Guid.CreateVersion7();

        object user = UserId.From(raw);
        object session = SessionId.From(raw);

        Assert.NotEqual(user, session);
    }

    [Fact]
    public void ToString_RendersTheUnderlyingGuid()
    {
        Guid raw = Guid.CreateVersion7();

        Assert.Equal(raw.ToString(), SessionId.From(raw).ToString());
    }

    [Fact]
    public void DefaultId_IsTheEmptyGuid()
    {
        Assert.Equal(Guid.Empty, default(UserId).Value);
    }

    private static void AssertIdContract<TId>(Func<TId> newId)
        where TId : struct, ITypedId<TId>
    {
        string name = typeof(TId).Name;

        TId first = newId();
        TId second = newId();

        Assert.True(first.Value != Guid.Empty, name + " produced an empty guid.");
        Assert.True(first.Value.Version == 7, name + " produced a v" + first.Value.Version + " guid.");
        Assert.True(!first.Equals(second), name + " produced duplicate ids.");
        Assert.True(first.Equals(TId.From(first.Value)), name + " does not round-trip through From().");
    }
}
