using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;

namespace PartyUp.Application.Tests;

/// <summary>D-211: candidate options that share one length, which is what a start-times-only fixture means.</summary>
internal static class PollSpecs
{
    internal const int DefaultDurationMinutes = 120;

    internal static PollOptionSpec[] Of(params Instant[] startTimes) => OfLength(DefaultDurationMinutes, startTimes);

    internal static PollOptionSpec[] OfAll(IEnumerable<Instant> startTimes) => Of([.. startTimes]);

    internal static PollOptionSpec[] OfLength(int durationMinutes, params Instant[] startTimes) =>
        [.. startTimes.Select(start => new PollOptionSpec(start, Duration15.FromMinutes(durationMinutes)))];
}
