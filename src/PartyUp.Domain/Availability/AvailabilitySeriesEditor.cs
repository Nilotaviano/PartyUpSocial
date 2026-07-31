using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Availability;

/// <summary>
/// The three recurring-availability edit shapes of D-134/D-143 plus series deletion (D-025),
/// as pure functions the application layer persists.
/// </summary>
public static class AvailabilitySeriesEditor
{
    /// <summary>Delete-one: the occurrence is suppressed, the series is untouched.</summary>
    public static AvailabilityOccurrenceSkip DeleteOccurrence(AvailabilitySeries series, LocalDate occurrenceDate) =>
        AvailabilityOccurrenceSkip.For(series, occurrenceDate);

    /// <summary>Edit-one: a skip plus a materialized block carrying <c>SourceSeriesId</c>.</summary>
    public static OccurrenceEditResult EditOccurrence(
        AvailabilitySeries series,
        LocalDate occurrenceDate,
        TimeRange replacement) =>
        new(
            AvailabilityOccurrenceSkip.For(series, occurrenceDate),
            AvailabilityBlock.Materialize(series, occurrenceDate, replacement));

    /// <summary>
    /// Edit-this-and-future: split <strong>at the selected occurrence's date</strong> — day-granular,
    /// the forward-only boundary D-143 demands (§12-20). The original ends there (exclusive) and the
    /// successor starts there, so every earlier occurrence keeps the old pattern untouched; when the
    /// replacement moves the weekday, the successor's first occurrence is simply the first
    /// new-weekday date on or after the split.
    /// </summary>
    public static SeriesSplitResult SplitFromOccurrence(
        AvailabilitySeries series,
        LocalDate occurrenceDate,
        WeeklyPattern replacement,
        IEnumerable<AvailabilityOccurrenceSkip> skips,
        IEnumerable<AvailabilityBlock> materializedBlocks)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(skips);
        ArgumentNullException.ThrowIfNull(materializedBlocks);
        series.EnsureOccurrenceDate(occurrenceDate);

        LocalDate splitDate = occurrenceDate;
        LocalDate? originalUntil = series.EffectiveUntil;

        List<AvailabilityOccurrenceSkip> droppedSkips =
            [.. skips.Where(skip => skip.SeriesId == series.Id && skip.OccurrenceDate >= splitDate)];
        List<AvailabilityBlock> droppedBlocks =
        [
            .. materializedBlocks.Where(block =>
                block.SourceSeriesId == series.Id
                && block.SourceOccurrenceDate is { } date
                && date >= splitDate),
        ];

        series.TruncateAt(splitDate);
        AvailabilitySeries successor = series.Continue(replacement, splitDate, originalUntil);

        return new SeriesSplitResult(series, successor, splitDate, droppedSkips, droppedBlocks);
    }

    /// <summary>Series deletion (D-025) cascades to skips and materialized blocks (§12-21).</summary>
    public static SeriesDeletionResult DeleteSeries(
        AvailabilitySeries series,
        IEnumerable<AvailabilityOccurrenceSkip> skips,
        IEnumerable<AvailabilityBlock> materializedBlocks)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(skips);
        ArgumentNullException.ThrowIfNull(materializedBlocks);

        return new SeriesDeletionResult(
            series,
            [.. skips.Where(skip => skip.SeriesId == series.Id)],
            [.. materializedBlocks.Where(block => block.SourceSeriesId == series.Id)]);
    }
}
