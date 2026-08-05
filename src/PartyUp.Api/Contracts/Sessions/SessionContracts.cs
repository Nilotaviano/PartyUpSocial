using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Contracts.Sessions;

/// <summary>
/// Wire-shape converters shared by the session and guest contracts (PHASE4_DESIGN.md §8). Every enum
/// crosses the wire as a stable lowerCamel/PascalCase string, never as a raw numeric value that a
/// future reorder in <c>SessionEnums.cs</c> would silently renumber out from under a mobile client.
/// </summary>
public static class SessionWireFormat
{
    public static string Format(Instant instant) => InstantPattern.ExtendedIso.Format(instant);

    public static string Platform(Platform platform) => platform switch
    {
        Domain.Kernel.Platform.Steam => "steam",
        Domain.Kernel.Platform.PlayStation => "playStation",
        Domain.Kernel.Platform.Xbox => "xbox",
        Domain.Kernel.Platform.NotApplicable => "notApplicable",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    public static Domain.Kernel.Platform? ParsePlatform(string? value) => value switch
    {
        null => null,
        "steam" => Domain.Kernel.Platform.Steam,
        "playStation" => Domain.Kernel.Platform.PlayStation,
        "xbox" => Domain.Kernel.Platform.Xbox,
        "notApplicable" => Domain.Kernel.Platform.NotApplicable,
        _ => throw new FormatException($"Unknown platform '{value}'."),
    };

    public static string Status(SessionStatus status) => status switch
    {
        SessionStatus.Unconfirmed => "unconfirmed",
        SessionStatus.Confirmed => "confirmed",
        SessionStatus.Completed => "completed",
        SessionStatus.Expired => "expired",
        SessionStatus.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string ParticipationStatus(ParticipationStatus status) => status switch
    {
        Domain.Kernel.ParticipationStatus.Pending => "pending",
        Domain.Kernel.ParticipationStatus.Going => "going",
        Domain.Kernel.ParticipationStatus.Maybe => "maybe",
        Domain.Kernel.ParticipationStatus.Declined => "declined",
        Domain.Kernel.ParticipationStatus.Removed => "removed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>The §8 RSVP request vocabulary — a strict subset of the full roster status enum.</summary>
    public static Domain.Kernel.ParticipationStatus? ParseRsvpResponse(string? value) => value switch
    {
        "Going" => Domain.Kernel.ParticipationStatus.Going,
        "Maybe" => Domain.Kernel.ParticipationStatus.Maybe,
        "Declined" => Domain.Kernel.ParticipationStatus.Declined,
        _ => null,
    };

    public static string Origin(ParticipationOrigin origin) => origin switch
    {
        ParticipationOrigin.Invited => "invited",
        ParticipationOrigin.JoinRequest => "joinRequest",
        ParticipationOrigin.GuestLink => "guestLink",
        ParticipationOrigin.PollFinalization => "pollFinalization",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    public static string JoinRequestStatus(JoinRequestStatus status) => status switch
    {
        Domain.Kernel.JoinRequestStatus.Pending => "pending",
        Domain.Kernel.JoinRequestStatus.Approved => "approved",
        Domain.Kernel.JoinRequestStatus.Rejected => "rejected",
        Domain.Kernel.JoinRequestStatus.Withdrawn => "withdrawn",
        Domain.Kernel.JoinRequestStatus.Lapsed => "lapsed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>The §8 <c>SetSessionReminder</c> choice vocabulary; <c>null</c> clears the override.</summary>
    public static string? ReminderChoice(ReminderOffset? choice) => choice switch
    {
        null => null,
        ReminderOffset.Off => "Off",
        ReminderOffset.Min15 => "Minutes15",
        ReminderOffset.Min30 => "Minutes30",
        ReminderOffset.Hours1 => "Hours1",
        ReminderOffset.Hours3 => "Hours3",
        ReminderOffset.Hours12 => "Hours12",
        ReminderOffset.Hours24 => "Hours24",
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    /// <summary>Returns <c>false</c> for an unrecognized token so the caller can reject with 400.</summary>
    public static bool TryParseReminderChoice(string? value, out ReminderOffset? choice)
    {
        switch (value)
        {
            case null:
                choice = null;
                return true;
            case "Off":
                choice = ReminderOffset.Off;
                return true;
            case "Minutes15":
                choice = ReminderOffset.Min15;
                return true;
            case "Minutes30":
                choice = ReminderOffset.Min30;
                return true;
            case "Hours1":
                choice = ReminderOffset.Hours1;
                return true;
            case "Hours3":
                choice = ReminderOffset.Hours3;
                return true;
            case "Hours12":
                choice = ReminderOffset.Hours12;
                return true;
            case "Hours24":
                choice = ReminderOffset.Hours24;
                return true;
            default:
                choice = null;
                return false;
        }
    }
}

public sealed record GameRequest(string Name, long? SteamAppId)
{
    /// <summary>Null on an invalid shape; the caller maps that to <c>400 session_validation</c>.</summary>
    public SessionGame? ToDomain()
    {
        try
        {
            return SteamAppId is { } appId
                ? SessionGame.FromSteamCatalog(appId, GameName.Create(Name))
                : SessionGame.FreeText(GameName.Create(Name));
        }
        catch (DomainException)
        {
            return null;
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameResponse(string Name, long? SteamAppId)
{
    public static GameResponse From(SessionGame game) => new(game.Name.Value, game.SteamAppId);
}

/// <summary>
/// Applies <see cref="JsonNamingPolicy.CamelCase"/> to an enum's string representation via
/// <see cref="JsonStringEnumConverter{TEnum}"/> — that generic converter's own parameterless
/// constructor (the only one <see cref="JsonConverterAttribute"/> can invoke) has no naming-policy
/// overload, so this attribute builds the converter itself instead.
/// </summary>
public sealed class CamelCaseStringEnumConverterAttribute : JsonConverterAttribute
{
    public override JsonConverter CreateConverter(Type typeToConvert) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(JsonStringEnumConverter<>).MakeGenericType(typeToConvert),
            JsonNamingPolicy.CamelCase,
            false)!;
}

/// <summary>A-070's closed clear-list vocabulary, exactly as declared in OpenAPI.</summary>
[CamelCaseStringEnumConverter]
public enum ClearFieldRequest
{
    MaxCapacity,
    Notes,
    Game,
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSessionRequest(
    string? Title,
    string? StartTime,
    int? DurationMinutes,
    string? Platform,
    GameRequest? Game,
    string? Notes,
    int? MinAttendance,
    int? MaxCapacity,
    bool? IsPrivate,
    IReadOnlyList<Guid>? InviteeUserIds,
    int? GuestLinkCount);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditSessionRequest(
    string? Title,
    string? StartTime,
    int? DurationMinutes,
    string? Platform,
    GameRequest? Game,
    string? Notes,
    int? MinAttendance,
    int? MaxCapacity,
    bool? IsPrivate,
    IReadOnlyList<ClearFieldRequest>? Clear,
    string? IfMatch);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InviteSessionUsersRequest(IReadOnlyList<Guid> UserIds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSessionGuestLinksRequest(int Count);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RespondToSessionInvitationRequest(string Response);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetSessionReminderRequest(string? Choice);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuestLinkInvitationResponse(Guid GuestLinkId, string Url);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSessionResponse(
    SessionDetailResponse Session,
    IReadOnlyList<GuestLinkInvitationResponse> GuestLinks);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RosterEntryResponse(
    Guid ParticipationId,
    Guid? UserId,
    Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    string Status,
    bool IsWaitlisted,
    string Origin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReminderChoice)
{
    public static RosterEntryResponse From(RosterEntry entry) =>
        new(
            entry.ParticipationId.Value,
            entry.Participant.UserId?.Value,
            entry.Participant.GuestIdentityId?.Value,
            entry.DisplayName?.Value,
            SessionWireFormat.ParticipationStatus(entry.Status),
            entry.IsWaitlisted,
            SessionWireFormat.Origin(entry.Origin),
            SessionWireFormat.ReminderChoice(entry.ReminderChoice));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionDetailResponse(
    Guid Id,
    Guid OrganizerId,
    string Title,
    string StartTime,
    int DurationMinutes,
    string Platform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameResponse? Game,
    string Notes,
    int MinAttendance,
    int? MaxCapacity,
    bool IsPrivate,
    string Status,
    string CreatedAt,
    IReadOnlyList<RosterEntryResponse> Roster,
    string Version)
{
    public static SessionDetailResponse From(SessionDetail detail, string version) =>
        new(
            detail.Id.Value,
            detail.OrganizerId.Value,
            detail.Title.Value,
            SessionWireFormat.Format(detail.StartTime),
            detail.Duration.TotalMinutes,
            SessionWireFormat.Platform(detail.Platform),
            detail.Game is { } game ? GameResponse.From(game) : null,
            detail.Notes.Value,
            detail.MinAttendance,
            detail.MaxCapacity,
            detail.IsPrivate,
            SessionWireFormat.Status(detail.Status),
            SessionWireFormat.Format(detail.CreatedAt),
            detail.Roster.Select(RosterEntryResponse.From).ToArray(),
            version);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionSummaryResponse(
    Guid Id,
    Guid OrganizerId,
    string Title,
    string StartTime,
    int DurationMinutes,
    string Platform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameResponse? Game,
    string Status,
    bool IsPrivate,
    int? MaxCapacity,
    int ActiveOccupancy)
{
    public static SessionSummaryResponse From(SessionSummary summary) =>
        new(
            summary.Id.Value,
            summary.OrganizerId.Value,
            summary.Title.Value,
            SessionWireFormat.Format(summary.StartTime),
            summary.Duration.TotalMinutes,
            SessionWireFormat.Platform(summary.Platform),
            summary.Game is { } game ? GameResponse.From(game) : null,
            SessionWireFormat.Status(summary.Status),
            summary.IsPrivate,
            summary.MaxCapacity,
            summary.ActiveOccupancy);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ParticipationViewResponse(
    Guid ParticipationId,
    Guid? UserId,
    Guid? GuestIdentityId,
    string Status,
    bool IsWaitlisted,
    string Origin)
{
    public static ParticipationViewResponse From(Participation participation) =>
        new(
            participation.Id.Value,
            participation.UserId?.Value,
            participation.GuestIdentityId?.Value,
            SessionWireFormat.ParticipationStatus(participation.Status),
            participation.IsWaitlisted,
            SessionWireFormat.Origin(participation.Origin));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JoinRequestViewResponse(Guid Id, Guid RequesterId, string Status, string CreatedAt)
{
    public static JoinRequestViewResponse From(JoinRequest request) =>
        new(
            request.Id.Value,
            request.RequesterId.Value,
            SessionWireFormat.JoinRequestStatus(request.Status),
            SessionWireFormat.Format(request.CreatedAt));

    public static JoinRequestViewResponse From(JoinRequestSummary summary) =>
        new(
            summary.Id.Value,
            summary.RequesterId.Value,
            SessionWireFormat.JoinRequestStatus(summary.Status),
            SessionWireFormat.Format(summary.CreatedAt));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReminderViewResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Choice);

/// <summary>The A-072 pagination envelope: <c>{ items, nextCursor }</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionPageResponse<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class SessionPageResponse
{
    public static SessionPageResponse<TOut> From<TIn, TOut>(SessionPage<TIn> page, Func<TIn, TOut> map) =>
        new(page.Items.Select(map).ToArray(), page.NextCursor);
}
