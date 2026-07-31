using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class ParticipantRefTests
{
    [Fact]
    public void ForUser_CarriesTheUserIdOnly()
    {
        UserId user = UserId.New();

        ParticipantRef reference = ParticipantRef.ForUser(user);

        Assert.Equal(user, reference.UserId);
        Assert.Null(reference.GuestIdentityId);
        Assert.False(reference.IsGuest);
        Assert.True(reference.IsRegistered);
        Assert.Equal(user.Value, reference.Key);
    }

    [Fact]
    public void ForGuest_CarriesTheGuestIdentityIdOnly()
    {
        GuestIdentityId guest = GuestIdentityId.New();

        ParticipantRef reference = ParticipantRef.ForGuest(guest);

        Assert.Equal(guest, reference.GuestIdentityId);
        Assert.Null(reference.UserId);
        Assert.True(reference.IsGuest);
        Assert.False(reference.IsRegistered);
        Assert.Equal(guest.Value, reference.Key);
    }

    [Fact]
    public void Create_WithBothIds_Throws()
    {
        DomainException error = Assert.Throws<DomainException>(
            () => ParticipantRef.Create(UserId.New(), GuestIdentityId.New()));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_WithNeitherId_Throws()
    {
        DomainException error = Assert.Throws<DomainException>(() => ParticipantRef.Create(null, null));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_WithExactlyOneId_MatchesTheDedicatedFactory()
    {
        UserId user = UserId.New();
        GuestIdentityId guest = GuestIdentityId.New();

        Assert.Equal(ParticipantRef.ForUser(user), ParticipantRef.Create(user, null));
        Assert.Equal(ParticipantRef.ForGuest(guest), ParticipantRef.Create(null, guest));
    }

    [Fact]
    public void Equality_IsByReferencedIdentity()
    {
        UserId user = UserId.New();

        Assert.Equal(ParticipantRef.ForUser(user), ParticipantRef.ForUser(user));
        Assert.NotEqual(ParticipantRef.ForUser(user), ParticipantRef.ForUser(UserId.New()));
    }

    [Fact]
    public void UserAndGuest_SharingAnUnderlyingGuid_AreNotEqual()
    {
        Guid raw = Guid.CreateVersion7();

        ParticipantRef user = ParticipantRef.ForUser(UserId.From(raw));
        ParticipantRef guest = ParticipantRef.ForGuest(GuestIdentityId.From(raw));

        Assert.NotEqual(user, guest);
        Assert.Equal(user.Key, guest.Key);
    }

    [Fact]
    public void RequireUserId_OnAGuestReference_Throws()
    {
        ParticipantRef guest = ParticipantRef.ForGuest(GuestIdentityId.New());

        DomainException error = Assert.Throws<DomainException>(() => guest.RequireUserId());

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void RequireGuestIdentityId_OnAUserReference_Throws()
    {
        ParticipantRef user = ParticipantRef.ForUser(UserId.New());

        DomainException error = Assert.Throws<DomainException>(() => user.RequireGuestIdentityId());

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void RequireAccessors_ReturnTheWrappedId()
    {
        UserId user = UserId.New();
        GuestIdentityId guest = GuestIdentityId.New();

        Assert.Equal(user, ParticipantRef.ForUser(user).RequireUserId());
        Assert.Equal(guest, ParticipantRef.ForGuest(guest).RequireGuestIdentityId());
    }

    [Fact]
    public void CompareTo_IsATotalOrderUsableAsAFinalizationTieBreak_D119()
    {
        ParticipantRef[] references =
        [
            ParticipantRef.ForUser(UserId.New()),
            ParticipantRef.ForGuest(GuestIdentityId.New()),
            ParticipantRef.ForUser(UserId.New()),
            ParticipantRef.ForGuest(GuestIdentityId.New()),
        ];

        List<ParticipantRef> once = [.. references];
        once.Sort();
        List<ParticipantRef> twice = [.. references.Reverse()];
        twice.Sort();

        Assert.Equal(once, twice);
        for (int i = 1; i < once.Count; i++)
        {
            Assert.True(once[i - 1] < once[i]);
            Assert.True(once[i] > once[i - 1]);
            Assert.True(once[i - 1] <= once[i]);
            Assert.True(once[i] >= once[i - 1]);
        }
    }

    [Fact]
    public void CompareTo_BreaksAKeyTieByGuestFlag()
    {
        Guid raw = Guid.CreateVersion7();
        ParticipantRef user = ParticipantRef.ForUser(UserId.From(raw));
        ParticipantRef guest = ParticipantRef.ForGuest(GuestIdentityId.From(raw));

        Assert.True(user < guest);
        Assert.Equal(0, user.CompareTo(ParticipantRef.ForUser(UserId.From(raw))));
    }

    [Fact]
    public void ToString_DistinguishesUsersFromGuests()
    {
        Guid raw = Guid.CreateVersion7();

        Assert.Equal("user:" + raw, ParticipantRef.ForUser(UserId.From(raw)).ToString());
        Assert.Equal("guest:" + raw, ParticipantRef.ForGuest(GuestIdentityId.From(raw)).ToString());
    }

    [Fact]
    public void DefaultReference_ReferencesNobody()
    {
        ParticipantRef unset = default;

        Assert.False(unset.IsGuest);
        Assert.False(unset.IsRegistered);
        Assert.Equal(Guid.Empty, unset.Key);
    }
}
