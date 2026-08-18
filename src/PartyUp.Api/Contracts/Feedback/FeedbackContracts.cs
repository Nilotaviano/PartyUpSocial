using System.Text.Json.Serialization;
using NodaTime.Text;
using PartyUp.Domain.Feedback;

namespace PartyUp.Api.Contracts.Feedback;

/// <summary>
/// D-212's submission. <see cref="Type"/> is the wire name of <see cref="FeedbackType"/>
/// (<c>bug</c> | <c>idea</c> | <c>other</c>); every context field is optional and may be omitted or
/// null, so a body carrying only a type and a message is complete.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitFeedbackRequest(
    string Type,
    string Message,
    string? Route = null,
    string? UserAgent = null,
    string? Locale = null,
    string? AppVersion = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitFeedbackResponse(Guid Id);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetFeedbackHandledRequest(bool Handled);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminFeedbackListResponse(
    IReadOnlyList<AdminFeedbackResponse> Items,
    int Page,
    int PageSize,
    int Total);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminFeedbackResponse(
    Guid Id,
    string Type,
    string Message,
    Guid? SubmitterUserId,
    string? Route,
    string? UserAgent,
    string? Locale,
    string? AppVersion,
    string CreatedAt,
    bool Handled,
    string? HandledAt)
{
    internal static AdminFeedbackResponse From(FeedbackItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new AdminFeedbackResponse(
            item.Id.Value,
            FeedbackTypeNames.ToWire(item.Type),
            item.Message,
            item.SubmitterUserId?.Value,
            item.Route,
            item.UserAgent,
            item.Locale,
            item.AppVersion,
            InstantPattern.ExtendedIso.Format(item.CreatedAt),
            item.Handled,
            item.HandledAt is { } handledAt
                ? InstantPattern.ExtendedIso.Format(handledAt)
                : null);
    }
}

/// <summary>The one wire vocabulary for <see cref="FeedbackType"/>, shared by both directions.</summary>
public static class FeedbackTypeNames
{
    public static string ToWire(FeedbackType type) => type switch
    {
        FeedbackType.Bug => "bug",
        FeedbackType.Idea => "idea",
        FeedbackType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static FeedbackType? FromWire(string? value) => value switch
    {
        "bug" => FeedbackType.Bug,
        "idea" => FeedbackType.Idea,
        "other" => FeedbackType.Other,
        _ => null,
    };
}
