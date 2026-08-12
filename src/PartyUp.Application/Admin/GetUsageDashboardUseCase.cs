using NodaTime;
using PartyUp.Application.Abstractions;

namespace PartyUp.Application.Admin;

/// <summary>
/// D-178: the five launch measures over 7- and 30-day windows, plus D-151's 30-day repeat
/// scheduling. Rates are fractions in [0, 1] and are null when their denominator is zero — an
/// undefined rate is reported as undefined rather than as 0.
/// </summary>
public sealed class GetUsageDashboardUseCase(IUsageDashboardReader reader, IClock clock)
{
    public static readonly Duration ShortWindow = Duration.FromDays(7);
    public static readonly Duration LongWindow = Duration.FromDays(30);

    public async Task<UsageDashboard> ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant now = clock.GetCurrentInstant();
        UsageWindow shortWindow = await ReadAsync(now, ShortWindow, cancellationToken);
        UsageWindow longWindow = await ReadAsync(now, LongWindow, cancellationToken);
        RepeatSchedulingMeasures repeat = await reader.ReadRepeatSchedulingAsync(
            LongWindow,
            cancellationToken);

        return new UsageDashboard(
            now,
            shortWindow,
            longWindow,
            new RepeatScheduling(
                (int)LongWindow.Days,
                repeat.Organizers,
                repeat.RepeatOrganizers,
                Rate(repeat.RepeatOrganizers, repeat.Organizers)));
    }

    private async Task<UsageWindow> ReadAsync(
        Instant now,
        Duration window,
        CancellationToken cancellationToken)
    {
        Instant since = now - window;
        UsageWindowMeasures measures = await reader.ReadWindowAsync(since, cancellationToken);
        return new UsageWindow(
            (int)window.Days,
            since,
            measures.Registrations,
            measures.ActiveUsers,
            measures.SessionsCreated,
            measures.PollsCreated,
            measures.ConfirmationDenominator,
            measures.ConfirmationSuccesses,
            Rate(measures.ConfirmationSuccesses, measures.ConfirmationDenominator));
    }

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}

public sealed record UsageDashboard(
    Instant GeneratedAt,
    UsageWindow Last7Days,
    UsageWindow Last30Days,
    RepeatScheduling RepeatScheduling);

public sealed record UsageWindow(
    int Days,
    Instant Since,
    int Registrations,
    int ActiveUsers,
    int SessionsCreated,
    int PollsCreated,
    int ConfirmationDenominator,
    int ConfirmationSuccesses,
    double? ConfirmationRate);

public sealed record RepeatScheduling(
    int Days,
    int Organizers,
    int RepeatOrganizers,
    double? Rate);
