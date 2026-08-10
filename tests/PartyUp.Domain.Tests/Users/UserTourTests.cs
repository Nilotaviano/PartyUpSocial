using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Users;

/// <summary>TOUR_DESIGN.md §1/§4 (D-182): first instant wins, idempotent.</summary>
public sealed class UserTourTests
{
    [Fact]
    public void CompleteTour_SetsTourCompletedAt_D182()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);
        Assert.Null(user.TourCompletedAt);

        user.CompleteTour(clock);

        Assert.Equal(TestUsers.Origin, user.TourCompletedAt);
    }

    [Fact]
    public void CompleteTour_IsIdempotent_FirstInstantWins_D182()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        user.CompleteTour(clock);
        clock.Advance(Duration.FromDays(1));
        user.CompleteTour(clock);

        Assert.Equal(TestUsers.Origin, user.TourCompletedAt);
    }
}
