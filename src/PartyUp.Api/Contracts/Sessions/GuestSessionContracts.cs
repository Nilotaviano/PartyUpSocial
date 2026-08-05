using System.Text.Json.Serialization;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Contracts.Sessions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClaimGuestSessionRequest(string? DisplayName, string? Response);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameGuestSessionRequest(string? DisplayName);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RespondAsGuestRequest(string? Response);

/// <summary>
/// §8.1's <c>GuestSessionView</c>-or-terminal-notice response. A-074's terminal branch carries only
/// <see cref="State"/> and <see cref="SessionId"/> — never roster, notes, or any other live field, so
/// a stale-credential bearer learns nothing beyond "this link is done" (D-167).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GuestSessionResponse(
    string State,
    Guid SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StartTime,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Platform,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GameResponse? Game,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? GuestIdentityId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Response)
{
    public const string LiveState = "live";

    public static GuestSessionResponse Live(GuestSessionView view) =>
        new(
            LiveState,
            view.SessionId.Value,
            view.Title.Value,
            SessionWireFormat.Format(view.StartTime),
            view.Duration.TotalMinutes,
            SessionWireFormat.Platform(view.Platform),
            view.Game is { } game ? GameResponse.From(game) : null,
            view.Notes.Value,
            view.GuestIdentityId?.Value,
            view.DisplayName?.Value,
            view.Response is { } response ? SessionWireFormat.ParticipationStatus(response) : null);

    public static GuestSessionResponse Terminal(SessionId sessionId, GuestLinkView notice) =>
        new(NoticeState(notice), sessionId.Value, null, null, null, null, null, null, null, null, null);

    private static string NoticeState(GuestLinkView notice) => notice switch
    {
        GuestLinkView.CanceledNotice => "canceled",
        GuestLinkView.RevokedNotice => "revoked",
        GuestLinkView.EndedNotice => "ended",
        GuestLinkView.PollExpiredNotice => "pollExpired",
        _ => throw new ArgumentOutOfRangeException(nameof(notice)),
    };
}
