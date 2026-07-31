using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

public class UsernameRegistryTests
{
    [Fact]
    public void ReservationPeriod_IsThirtyDays_D145()
    {
        Assert.Equal(Duration.FromDays(30), UsernameRegistry.ReservationPeriod);
    }

    [Fact]
    public void Canonicalize_TrimsAndLowerCases_D128()
    {
        Assert.Equal("gamer", UsernameRegistry.Canonicalize(Username.Create("  GaMeR  ")));
        Assert.Equal("gamer", UsernameRegistry.Canonicalize("GAMER"));
        Assert.Equal(string.Empty, UsernameRegistry.Canonicalize((string?)null));
    }

    [Fact]
    public void SameName_ComparesCaseInsensitively_D128()
    {
        Assert.True(UsernameRegistry.SameName(Username.Create("GAMER"), Username.Create("gamer")));
        Assert.False(UsernameRegistry.SameName(Username.Create("gamer"), Username.Create("gamers")));
    }

    [Fact]
    public void Claim_CreatesAnActiveRowWhenNothingHoldsTheName_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();

        UsernameRegistryEntry entry = UsernameRegistry.Claim(null, Username.Create("GaMeR"), owner, clock);

        Assert.Equal("gamer", entry.NameCanonical);
        Assert.True(entry.IsActive);
        Assert.Equal(owner, entry.OwnerId);
        Assert.Null(entry.ReservedUntil);
    }

    [Fact]
    public void Claim_RejectsANameAnotherUserActivelyHolds_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry held = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            UsernameRegistry.Claim(held, Username.Create("GAMER"), UserId.New(), clock));

        Assert.Equal(DomainError.Duplicate, error.Error);
    }

    [Fact]
    public void Claim_IsIdempotentForTheOwnerAlreadyHoldingTheName_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry held = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);

        Assert.Same(held, UsernameRegistry.Claim(held, Username.Create("gamer"), owner, clock));
    }

    [Fact]
    public void Claim_RejectsARowHoldingADifferentName_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry held = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            UsernameRegistry.Claim(held, Username.Create("other"), UserId.New(), clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Claim_RejectsAReservedNameBeforeItIsFree_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry reserved = Reserved(clock, "gamer");

        clock.Advance(UsernameRegistry.ReservationPeriod - Duration.FromNanoseconds(1L));

        DomainException error = Assert.Throws<DomainException>(() =>
            UsernameRegistry.Claim(reserved, Username.Create("gamer"), UserId.New(), clock));

        Assert.Equal(DomainError.NameReserved, error.Error);
        Assert.True(reserved.IsReserved);
    }

    [Fact]
    public void Claim_TakesAReservedNameAtExactlyItsFreeInstant_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry reserved = Reserved(clock, "gamer");
        UserId newOwner = UserId.New();

        clock.Advance(UsernameRegistry.ReservationPeriod);

        UsernameRegistryEntry claimed = UsernameRegistry.Claim(reserved, Username.Create("gamer"), newOwner, clock);

        Assert.Same(reserved, claimed);
        Assert.True(claimed.IsActive);
        Assert.Equal(newOwner, claimed.OwnerId);
        Assert.Null(claimed.ReservedUntil);
    }

    [Fact]
    public void IsClaimable_TracksTheFreeAfterInstant_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry active = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        Assert.False(active.IsClaimable(clock));

        UsernameRegistryEntry reserved = Reserved(clock, "other");

        Assert.False(reserved.IsClaimable(clock));

        clock.Advance(UsernameRegistry.ReservationPeriod);

        Assert.True(reserved.IsClaimable(clock));
    }

    [Fact]
    public void Change_FlipsTheOldNameToAThirtyDayReservationAndClaimsTheNew_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry current = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);

        UsernameChangeResult result = UsernameRegistry.Change(current, null, Username.Create("newname"), owner, clock);

        Assert.Same(current, result.ReleasedEntry);
        Assert.False(result.IsCaseOnlyRestyle);
        Assert.True(current.IsReserved);
        Assert.Null(current.OwnerId);
        Assert.Equal(TestUsers.Origin + UsernameRegistry.ReservationPeriod, current.ReservedUntil);
        Assert.Equal("newname", result.ClaimedEntry.NameCanonical);
        Assert.Equal(owner, result.ClaimedEntry.OwnerId);
    }

    [Fact]
    public void Change_LeavesTheOldRowActiveWhenTheNewNameIsTaken_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry current = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);
        UsernameRegistryEntry taken = UsernameRegistry.Claim(null, Username.Create("newname"), UserId.New(), clock);

        Assert.Throws<DomainException>(() =>
            UsernameRegistry.Change(current, taken, Username.Create("newname"), owner, clock));

        Assert.True(current.IsActive);
        Assert.Equal(owner, current.OwnerId);
    }

    [Fact]
    public void Change_LeavesTheOldRowActiveWhenTheNewNameIsStillReserved_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry current = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);
        UsernameRegistryEntry reserved = Reserved(clock, "newname");

        DomainException error = Assert.Throws<DomainException>(() =>
            UsernameRegistry.Change(current, reserved, Username.Create("newname"), owner, clock));

        Assert.Equal(DomainError.NameReserved, error.Error);
        Assert.True(current.IsActive);
    }

    [Fact]
    public void Change_ReclaimsALapsedReservation_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry current = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);
        UsernameRegistryEntry reserved = Reserved(clock, "newname");

        clock.Advance(UsernameRegistry.ReservationPeriod);

        UsernameChangeResult result = UsernameRegistry.Change(current, reserved, Username.Create("newname"), owner, clock);

        Assert.Same(reserved, result.ClaimedEntry);
        Assert.True(reserved.IsActive);
        Assert.True(current.IsReserved);
    }

    [Fact]
    public void Change_ToADifferentCasingOfTheSameNameReservesNothing_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry current = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);

        UsernameChangeResult result = UsernameRegistry.Change(current, null, Username.Create("GaMeR"), owner, clock);

        Assert.True(result.IsCaseOnlyRestyle);
        Assert.Same(current, result.ClaimedEntry);
        Assert.True(current.IsActive);
        Assert.Equal(owner, current.OwnerId);
    }

    [Fact]
    public void Change_RejectsANonOwner_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry current = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        DomainException error = Assert.Throws<DomainException>(() =>
            UsernameRegistry.Change(current, null, Username.Create("newname"), UserId.New(), clock));

        Assert.Equal(DomainError.Forbidden, error.Error);
        Assert.True(current.IsActive);
    }

    [Fact]
    public void Change_RejectsAReservedCurrentRow_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry reserved = Reserved(clock, "gamer");

        DomainException error = Assert.Throws<DomainException>(() =>
            UsernameRegistry.Change(reserved, null, Username.Create("newname"), UserId.New(), clock));

        Assert.Equal(DomainError.Forbidden, error.Error);
    }

    [Fact]
    public void Release_TurnsTheNameIntoAnOwnerlessReservation_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UserId owner = UserId.New();
        UsernameRegistryEntry entry = UsernameRegistry.Claim(null, Username.Create("gamer"), owner, clock);

        UsernameRegistry.Release(entry, owner, clock);

        Assert.True(entry.IsReserved);
        Assert.Null(entry.OwnerId);
        Assert.Equal(TestUsers.Origin + UsernameRegistry.ReservationPeriod, entry.ReservedUntil);
        Assert.Equal("gamer", entry.NameCanonical);
    }

    [Fact]
    public void Release_RejectsANonOwner_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry entry = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        Assert.Equal(
            DomainError.Forbidden,
            Assert.Throws<DomainException>(() => UsernameRegistry.Release(entry, UserId.New(), clock)).Error);
    }

    [Fact]
    public void Reserve_RejectsAnAlreadyReservedRow_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry reserved = Reserved(clock, "gamer");

        Assert.Equal(
            DomainError.InvalidTransition,
            Assert.Throws<DomainException>(() => reserved.Reserve(clock)).Error);
    }

    [Fact]
    public void ClaimFrom_RejectsAnActiveRow_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry active = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        Assert.Equal(
            DomainError.Duplicate,
            Assert.Throws<DomainException>(() => active.ClaimFrom(UserId.New(), clock)).Error);
    }

    [Fact]
    public void ClaimNew_RejectsAnUnsetName_D128()
    {
        Assert.Equal(
            DomainError.ValidationFailed,
            Assert.Throws<DomainException>(() => UsernameRegistryEntry.ClaimNew(default, UserId.New())).Error);
    }

    [Fact]
    public void Matches_ComparesTheCanonicalForm_D128()
    {
        FakeClock clock = TestUsers.Clock();
        UsernameRegistryEntry entry = UsernameRegistry.Claim(null, Username.Create("gamer"), UserId.New(), clock);

        Assert.True(entry.Matches(Username.Create("GAMER")));
        Assert.False(entry.Matches(Username.Create("gamers")));
    }

    [Fact]
    public void AChangedThenReClaimedName_TravelsThroughExactlyOneConstraintDomain_D145()
    {
        FakeClock clock = TestUsers.Clock();
        UserId first = UserId.New();
        UserId second = UserId.New();
        UsernameRegistryEntry gamer = UsernameRegistry.Claim(null, Username.Create("gamer"), first, clock);
        UsernameChangeResult change = UsernameRegistry.Change(gamer, null, Username.Create("newname"), first, clock);

        Assert.Throws<DomainException>(() => UsernameRegistry.Claim(gamer, Username.Create("gamer"), second, clock));

        clock.Advance(UsernameRegistry.ReservationPeriod);
        UsernameRegistryEntry reclaimed = UsernameRegistry.Claim(gamer, Username.Create("gamer"), second, clock);

        Assert.Same(gamer, reclaimed);
        Assert.Equal(second, reclaimed.OwnerId);
        Assert.Equal(first, change.ClaimedEntry.OwnerId);
    }

    private static UsernameRegistryEntry Reserved(FakeClock clock, string name)
    {
        UserId owner = UserId.New();
        UsernameRegistryEntry entry = UsernameRegistry.Claim(null, Username.Create(name), owner, clock);
        UsernameRegistry.Release(entry, owner, clock);
        return entry;
    }
}
