using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Application.Availability;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Contracts.Availability;

/// <summary>
/// Wire-shape converters for the availability surface (PHASE5_DESIGN.md §8/§8.1). NodaTime types never
/// cross the wire directly — every value is formatted through here, mirroring
/// <c>SessionWireFormat</c>'s convention (PHASE4_DESIGN.md §8).
/// </summary>
public static class AvailabilityWireFormat
{
    private static readonly LocalDatePattern DatePattern = LocalDatePattern.Iso;
    private static readonly LocalTimePattern TimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm");

    public static string Format(Instant instant) => InstantPattern.ExtendedIso.Format(instant);

    public static string Format(LocalDate date) => DatePattern.Format(date);

    public static string Format(LocalTime time) => TimePattern.Format(time);

    public static bool TryParseInstant(string? value, out Instant instant)
    {
        if (value is not null)
        {
            var parsed = InstantPattern.ExtendedIso.Parse(value);
            if (parsed.Success)
            {
                instant = parsed.Value;
                return true;
            }
        }

        instant = default;
        return false;
    }

    public static bool TryParseDate(string? value, out LocalDate date)
    {
        if (value is not null)
        {
            var parsed = DatePattern.Parse(value);
            if (parsed.Success)
            {
                date = parsed.Value;
                return true;
            }
        }

        date = default;
        return false;
    }

    public static bool TryParseTime(string? value, out LocalTime time)
    {
        if (value is not null)
        {
            var parsed = TimePattern.Parse(value);
            if (parsed.Success)
            {
                time = parsed.Value;
                return true;
            }
        }

        time = default;
        return false;
    }
}

/// <summary>
/// PHASE5_DESIGN.md §8.1: the wire shape of every person Phase 5 renders. <see cref="Ordinal"/> is
/// 0-based, assigned within one response, and per-person-stable inside that response only — meaningless
/// across responses. Carries no user id, ever.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MaskedParticipant(int Ordinal, string? DisplayName);

/// <summary>
/// Assigns 0-based ordinals to distinct people within one response, stable for the lifetime of this
/// instance only (one instance per HTTP response composition, PHASE5_DESIGN.md §8.1) — a person seen
/// twice in one response (a friend-availability owner who is also a visible session's organizer, a
/// requester appearing on two join-request rows, …) carries the same ordinal both times; a different
/// person always gets the next one — every allocation goes through one shared counter, so two
/// distinct organizers in one page can never both read as person 0 (a client correlating an
/// inherited row's absent <c>organizerId</c> against a friend-layer owner must never conclude they
/// were the same person).
/// </summary>
public sealed class MaskedParticipantAssigner(IReadOnlyDictionary<UserId, DisplayName?>? knownDisplayNames = null)
{
    private readonly IReadOnlyDictionary<UserId, DisplayName?> _knownDisplayNames =
        knownDisplayNames ?? new Dictionary<UserId, DisplayName?>();
    private readonly Dictionary<UserId, int> _ordinalsByUserId = [];
    private int _nextOrdinal;

    /// <summary>Looks the display name up in the dictionary this assigner was built with (the friend-set
    /// contexts: overlap windows, considered friends, calendar friend-availability owners).</summary>
    public MaskedParticipant For(UserId id) =>
        For(id, _knownDisplayNames.TryGetValue(id, out DisplayName? name) ? name : null);

    /// <summary>A caller-resolved display name (session organizer, join-request requester) that is not
    /// necessarily in the friend-set dictionary — an unattached Discover/calendar organizer is a
    /// stranger to the viewer, never a friend, and would resolve to nothing from that dictionary.</summary>
    public MaskedParticipant For(UserId id, DisplayName? displayName)
    {
        if (!_ordinalsByUserId.TryGetValue(id, out int ordinal))
        {
            ordinal = _nextOrdinal++;
            _ordinalsByUserId[id] = ordinal;
        }

        return new MaskedParticipant(ordinal, displayName?.Value);
    }

    public IReadOnlyList<MaskedParticipant> For(IEnumerable<UserId> ids) => [.. ids.Select(id => For(id))];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TimeRangeView(string Start, string End)
{
    public static TimeRangeView From(TimeRange range) =>
        new(AvailabilityWireFormat.Format(range.Start), AvailabilityWireFormat.Format(range.End));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OverlapWindowView(
    string Start,
    string End,
    IReadOnlyList<MaskedParticipant> Available,
    IReadOnlyList<MaskedParticipant> Maybe)
{
    public static OverlapWindowView From(OverlapWindow window, MaskedParticipantAssigner ordinals) =>
        new(
            AvailabilityWireFormat.Format(window.Range.Start),
            AvailabilityWireFormat.Format(window.Range.End),
            ordinals.For(window.Available),
            ordinals.For(window.Maybe));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendAvailabilityEntryResponse(MaskedParticipant Owner, IReadOnlyList<TimeRangeView> Ranges)
{
    public static FriendAvailabilityEntryResponse From(FriendAvailabilityEntry entry, MaskedParticipantAssigner ordinals) =>
        new(ordinals.For(entry.OwnerId), [.. entry.Ranges.Select(TimeRangeView.From)]);
}

/// <summary>A-080: the <c>blockId</c> xor <c>(seriesId, occurrenceDate)</c> authoring identity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvailabilityOccurrenceOriginView(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BlockId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SeriesId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OccurrenceDate)
{
    public static AvailabilityOccurrenceOriginView From(AvailabilityOccurrenceOrigin origin) => origin switch
    {
        AvailabilityOccurrenceOrigin.FromBlock fromBlock =>
            new AvailabilityOccurrenceOriginView("block", fromBlock.BlockId.Value, null, null),
        AvailabilityOccurrenceOrigin.FromSeriesOccurrence fromOccurrence =>
            new AvailabilityOccurrenceOriginView(
                "occurrence",
                null,
                fromOccurrence.SeriesId.Value,
                AvailabilityWireFormat.Format(fromOccurrence.OccurrenceDate)),
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MyAvailabilityOccurrenceView(
    string StartTime,
    string EndTime,
    AvailabilityOccurrenceOriginView Origin)
{
    public static MyAvailabilityOccurrenceView From(MyAvailabilityOccurrence occurrence) =>
        new(
            AvailabilityWireFormat.Format(occurrence.Range.Start),
            AvailabilityWireFormat.Format(occurrence.Range.End),
            AvailabilityOccurrenceOriginView.From(occurrence.Origin));
}

/// <summary>
/// PHASE5_DESIGN.md §5.1/§8.1: <c>{occurrences, merged}</c>, D-101's two forms, plus the owner's
/// <see cref="Series"/> definitions (D-025's addressable rules). <see cref="Range"/> is present only on
/// the standalone <c>GET /me/availability</c> response — <c>GET /calendar</c> echoes one range for all
/// four layers instead. <see cref="Series"/> is never omitted: an empty list serializes as <c>[]</c> so
/// a client never distinguishes absent from empty.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MyAvailabilityLayerResponse(
    IReadOnlyList<MyAvailabilityOccurrenceView> Occurrences,
    IReadOnlyList<TimeRangeView> Merged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimeRangeView? Range,
    IReadOnlyList<AvailabilitySeriesView> Series)
{
    public static MyAvailabilityLayerResponse From(MyAvailabilityLayer layer, bool includeRange) =>
        new(
            [.. layer.Occurrences.Select(MyAvailabilityOccurrenceView.From)],
            [.. layer.Merged.Select(TimeRangeView.From)],
            includeRange ? TimeRangeView.From(layer.Range) : null,
            [.. layer.Series.Select(AvailabilitySeriesView.From)]);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvailabilityBlockSourceView(Guid SeriesId, string OccurrenceDate);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvailabilityBlockView(
    Guid BlockId,
    string StartTime,
    string EndTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvailabilityBlockSourceView? Source)
{
    public static AvailabilityBlockView From(AvailabilityBlock block) =>
        new(
            block.Id.Value,
            AvailabilityWireFormat.Format(block.Start),
            AvailabilityWireFormat.Format(block.End),
            block.SourceSeriesId is { } seriesId && block.SourceOccurrenceDate is { } occurrenceDate
                ? new AvailabilityBlockSourceView(seriesId.Value, AvailabilityWireFormat.Format(occurrenceDate))
                : null);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AvailabilitySeriesView(
    Guid SeriesId,
    int DayOfWeek,
    string StartTime,
    int DurationMinutes,
    string ZoneId,
    string EffectiveFrom,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EffectiveUntil)
{
    public static AvailabilitySeriesView From(AvailabilitySeries series) =>
        new(
            series.Id.Value,
            (int)series.DayOfWeek,
            AvailabilityWireFormat.Format(series.StartTime),
            series.Length.TotalMinutes,
            series.ZoneId,
            AvailabilityWireFormat.Format(series.EffectiveFrom),
            series.EffectiveUntil is { } until ? AvailabilityWireFormat.Format(until) : null);
}

/// <summary>
/// PHASE5_DESIGN.md §8.1: the closed discriminated response for <c>PATCH
/// …/occurrences/{occurrenceDate}</c>, wire-discriminated on <c>scope</c>, so
/// <c>EditOccurrenceRequest</c>'s response side carries a real schema rather than an untyped
/// <c>object</c>. <c>OnlyThis</c> carries only <c>block</c>; <c>ThisAndFuture</c> carries
/// <c>original</c> (nullable — <c>null</c> exactly when the split emptied the source series) and
/// <c>successor</c>; the two arms never share a payload shape (§8.1's table).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "scope")]
[JsonDerivedType(typeof(OnlyThis), "onlyThis")]
[JsonDerivedType(typeof(ThisAndFuture), "thisAndFuture")]
public abstract record EditOccurrenceResponse
{
    private EditOccurrenceResponse()
    {
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record OnlyThis(AvailabilityBlockView Block) : EditOccurrenceResponse;

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ThisAndFuture(AvailabilitySeriesView? Original, AvailabilitySeriesView Successor)
        : EditOccurrenceResponse;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OverlapSearchResponse(
    IReadOnlyList<OverlapWindowView> Windows,
    TimeRangeView Range,
    string SelectionSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MaskedParticipant>? ConsideredFriends);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HomeSuggestionsResponse(IReadOnlyList<OverlapWindowView> Suggestions, bool HasMore);

// ---- requests ------------------------------------------------------------------------------------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAvailabilityBlockRequest(string? StartTime, string? EndTime);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditAvailabilityBlockRequest(string? StartTime, string? EndTime);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAvailabilitySeriesRequest(
    int? DayOfWeek,
    string? StartTime,
    int? DurationMinutes,
    string? EffectiveFrom,
    string? EffectiveUntil);

/// <summary>
/// PHASE5_DESIGN.md §8.1: the closed discriminated union on <c>scope</c>. <c>startTime</c> carries an
/// <see cref="Instant"/> string for <c>onlyThis</c> and a <see cref="LocalTime"/> "HH:mm" string for
/// <c>thisAndFuture</c> — the two scopes never share a body shape, so the field is bound as a raw
/// string and parsed per-scope by the endpoint. <c>zoneId</c> is deliberately not a member (§8.1).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditOccurrenceRequest(
    string? Scope,
    string? StartTime,
    string? EndTime,
    int? DayOfWeek,
    int? DurationMinutes);
