using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using static PartyUp.Domain.Tests.Availability.AvailabilityTestData;

namespace PartyUp.Domain.Tests.Availability;

/// <summary>
/// The overlap engine's prefill consumers (DOMAIN_MODEL §8): D-102's clamp up to the D-130 minimum
/// lead, and D-109's over-duration warning, which is reported and never enforced.
/// </summary>
public class RecommendationPrefillTests
{
    private static readonly UserId Ana = UserId.New();
    private static readonly UserId Bruno = UserId.New();

    private static Duration15 Length(int minutes) => Duration15.FromMinutes(minutes);

    private static FakeClock ClockAt(int hour, int minute) => new(At(BaseYear, 8, 3, hour, minute));

    [Fact]
    public void ForSession_KeepsAComfortablyFutureWindowStart_D102()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 21), Length(120), ClockAt(9, 0));

        Assert.Equal(At(BaseYear, 8, 3, 18, 0), prefill.StartTime);
        Assert.False(prefill.StartWasClamped);
        Assert.False(prefill.HasOverDurationWarning);
        Assert.Equal(Hours(18, 20), prefill.Range);
    }

    [Fact]
    public void ForSession_ClampsAPastWindowStartUpToTheMinimumLead_D102()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 22), Length(60), ClockAt(19, 0));

        Assert.Equal(At(BaseYear, 8, 3, 19, 15), prefill.StartTime);
        Assert.True(prefill.StartWasClamped);
    }

    [Fact]
    public void ForSession_ClampsAStartInsideTheMinimumLead_D102()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 22), Length(60), ClockAt(17, 50));

        Assert.Equal(At(BaseYear, 8, 3, 18, 5), prefill.StartTime);
        Assert.True(prefill.StartWasClamped);
    }

    [Fact]
    public void ForSession_KeepsAStartExactlyAtTheMinimumLead_D102()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 22), Length(60), ClockAt(17, 45));

        Assert.Equal(At(BaseYear, 8, 3, 18, 0), prefill.StartTime);
        Assert.False(prefill.StartWasClamped);
    }

    [Fact]
    public void ForSession_ClampsAWindowThatHasEntirelyElapsed_D102()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 20), Length(120), ClockAt(23, 0));

        Assert.Equal(At(BaseYear, 8, 3, 23, 15), prefill.StartTime);
        Assert.True(prefill.StartWasClamped);
        Assert.Equal(Duration.FromMinutes(315), prefill.OverDuration);
    }

    [Fact]
    public void ClampStart_NeverMovesAStartEarlier_D102()
    {
        Instant now = At(BaseYear, 8, 3, 9, 0);
        Instant windowStart = At(BaseYear, 8, 3, 18, 0);

        Assert.Equal(windowStart, RecommendationPrefill.ClampStart(windowStart, now));
        Assert.False(RecommendationPrefill.WouldClamp(windowStart, now));
        Assert.Equal(now + Duration.FromMinutes(15), RecommendationPrefill.EarliestStart(now));
    }

    [Fact]
    public void ForSession_PrefillsFromTheTopRankedRecommendation_D102()
    {
        FakeClock clock = ClockAt(19, 0);
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(18, 21)), Friend(Bruno, Hours(19, 22))],
            WholeDay);

        SessionPrefill prefill = RecommendationPrefill.ForSession(windows[0], Length(60), clock);

        Assert.Equal(Hours(19, 21), windows[0].Range);
        Assert.Equal(At(BaseYear, 8, 3, 19, 15), prefill.StartTime);
        Assert.True(prefill.StartWasClamped);
        Assert.False(prefill.HasOverDurationWarning);
    }

    [Fact]
    public void ForSession_ProducesAStartTheSessionWindowAccepts_D130()
    {
        foreach (int hour in new[] { 0, 9, 18, 19, 23 })
        {
            FakeClock clock = ClockAt(hour, 0);

            SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 20), Length(60), clock);

            Assert.True(SessionStartWindow.IsWithin(prefill.StartTime, clock.GetCurrentInstant()));
        }
    }

    [Fact]
    public void ForSession_RejectsAWindowBeyondTheOneYearCeiling_D130()
    {
        FakeClock clock = ClockAt(9, 0);
        TimeRange farFuture = TimeRange.FromDuration(
            clock.GetCurrentInstant() + Duration.FromDays(366),
            Duration.FromHours(2));

        DomainException error = Assert.Throws<DomainException>(
            () => RecommendationPrefill.ForSession(farFuture, Length(60), clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void ForPollOption_ClampsTheOptionInstantTheSameWay_D102()
    {
        PollOptionPrefill prefill = RecommendationPrefill.ForPollOption(Hours(18, 22), ClockAt(19, 0));

        Assert.Equal(At(BaseYear, 8, 3, 19, 15), prefill.StartTime);
        Assert.True(prefill.StartWasClamped);
    }

    [Fact]
    public void ForPollOption_KeepsAFutureOptionInstant_D102()
    {
        FakeClock clock = ClockAt(9, 0);
        IReadOnlyList<OverlapWindow> windows = OverlapEngine.FindWindows(
            [Friend(Ana, Hours(18, 21)), Friend(Bruno, Hours(19, 22))],
            WholeDay);

        PollOptionPrefill prefill = RecommendationPrefill.ForPollOption(windows[0], clock);

        Assert.Equal(At(BaseYear, 8, 3, 19, 0), prefill.StartTime);
        Assert.False(prefill.StartWasClamped);
    }

    [Fact]
    public void ForPollOption_RejectsAWindowBeyondTheOptionCeiling_D102()
    {
        FakeClock clock = ClockAt(9, 0);
        TimeRange farFuture = TimeRange.FromDuration(
            clock.GetCurrentInstant() + Duration.FromDays(366),
            Duration.FromHours(2));

        DomainException error = Assert.Throws<DomainException>(
            () => RecommendationPrefill.ForPollOption(farFuture, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void ForSession_AcceptsADurationLongerThanItsWindow_D109()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 20), Length(180), ClockAt(9, 0));

        Assert.Equal(At(BaseYear, 8, 3, 18, 0), prefill.StartTime);
        Assert.Equal(180, prefill.ChosenDuration.TotalMinutes);
        Assert.True(prefill.HasOverDurationWarning);
        Assert.Equal(Duration.FromHours(1), prefill.OverDuration);
    }

    [Fact]
    public void ForSession_KeepsTheChosenDurationOnATinyWindow_D109()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(
            Minutes(18, 0, 18, 15),
            Length(Duration15.MaximumMinutes),
            ClockAt(9, 0));

        Assert.Equal(Duration15.MaximumMinutes, prefill.ChosenDuration.TotalMinutes);
        Assert.True(prefill.HasOverDurationWarning);
        Assert.Equal(Duration.FromMinutes(Duration15.MaximumMinutes - 15), prefill.OverDuration);
    }

    [Fact]
    public void ForSession_WarnsAboutNoDurationThatExactlyFillsItsWindow_D109()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 20), Length(120), ClockAt(9, 0));

        Assert.False(prefill.HasOverDurationWarning);
        Assert.Equal(Duration.Zero, prefill.OverDuration);
    }

    [Fact]
    public void ForSession_WarnsAboutNoDurationShorterThanItsWindow_D109()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 22), Length(60), ClockAt(9, 0));

        Assert.False(prefill.HasOverDurationWarning);
        Assert.Equal(Duration.Zero, prefill.OverDuration);
    }

    [Fact]
    public void ForSession_MeasuresTheOverDurationFromTheClampedStart_D109()
    {
        SessionPrefill prefill = RecommendationPrefill.ForSession(Hours(18, 20), Length(60), ClockAt(19, 0));

        Assert.Equal(At(BaseYear, 8, 3, 19, 15), prefill.StartTime);
        Assert.True(prefill.HasOverDurationWarning);
        Assert.Equal(Duration.FromMinutes(15), prefill.OverDuration);
    }

    [Fact]
    public void OverDuration_IsZeroWhenTheSessionEndsExactlyAtTheWindowEnd_D109()
    {
        Duration overDuration = RecommendationPrefill.OverDuration(
            At(BaseYear, 8, 3, 19, 0),
            Length(60),
            Hours(18, 20));

        Assert.Equal(Duration.Zero, overDuration);
    }

    [Fact]
    public void OverDuration_CountsAStartAlreadyPastTheWindowEndInFull_D109()
    {
        Duration overDuration = RecommendationPrefill.OverDuration(
            At(BaseYear, 8, 3, 21, 0),
            Length(60),
            Hours(18, 20));

        Assert.Equal(Duration.FromHours(2), overDuration);
    }
}
