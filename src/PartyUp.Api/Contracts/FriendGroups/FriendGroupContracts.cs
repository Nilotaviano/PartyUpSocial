using System.Text.Json.Serialization;
using NodaTime.Text;
using PartyUp.Api.Endpoints;
using PartyUp.Application.FriendGroups;

namespace PartyUp.Api.Contracts.FriendGroups;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFriendGroupRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameFriendGroupRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InviteFriendGroupMembersRequest(IReadOnlyList<Guid> UserIds);

/// <summary>
/// The group-level fields every viewer with any visibility may see (D-199): id, name, owner, creation
/// time, and the humans-in-group count (the owner plus every <c>Accepted</c> member) — enough for a
/// list card to render a count/stack with no per-group detail fetch.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupResponse(
    Guid Id,
    string Name,
    Guid OwnerId,
    string CreatedAt,
    int MemberCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? OwnerAvatar,
    bool OwnerMasked)
{
    public static FriendGroupResponse From(FriendGroupSummary summary) =>
        new(
            summary.Id.Value,
            summary.Name,
            summary.OwnerId.Value,
            InstantPattern.ExtendedIso.Format(summary.CreatedAt),
            summary.MemberCount,
            summary.OwnerDisplayName?.Value,
            summary.OwnerAvatar is { } avatar ? AvatarResponse.From(avatar) : null,
            summary.OwnerMasked);
}

/// <summary>A pending invite the actor holds in someone else's group — name + owner only (D-199).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupInviteResponse(
    Guid Id,
    string Name,
    Guid OwnerId,
    string InvitedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? OwnerAvatar,
    bool OwnerMasked)
{
    public static FriendGroupInviteResponse From(FriendGroupInviteSummary summary) =>
        new(
            summary.Id.Value,
            summary.Name,
            summary.OwnerId.Value,
            InstantPattern.ExtendedIso.Format(summary.InvitedAt),
            summary.OwnerDisplayName?.Value,
            summary.OwnerAvatar is { } avatar ? AvatarResponse.From(avatar) : null,
            summary.OwnerMasked);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupListResponse(
    IReadOnlyList<FriendGroupResponse> Owned,
    IReadOnlyList<FriendGroupResponse> MemberOf,
    IReadOnlyList<FriendGroupInviteResponse> Invites)
{
    public static FriendGroupListResponse From(FriendGroupListResult result) =>
        new(
            result.Owned.Select(FriendGroupResponse.From).ToArray(),
            result.MemberOf.Select(FriendGroupResponse.From).ToArray(),
            result.Invites.Select(FriendGroupInviteResponse.From).ToArray());
}

/// <summary>
/// D-210: the groups the CALLER owns that a given user is an <c>Accepted</c> member of — the whole
/// answer in one round trip, in the same card shape the list read returns, so any client (web or
/// mobile) can render "still in: …" and offer the per-group removal without a roster fan-out.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupMembershipsResponse(IReadOnlyList<FriendGroupResponse> Groups)
{
    public static FriendGroupMembershipsResponse From(IReadOnlyList<FriendGroupSummary> groups) =>
        new([.. groups.Select(FriendGroupResponse.From)]);
}

/// <summary>
/// One roster row (D-199): "invited" | "accepted" on the wire. <c>Masked</c> is D-076's render-time
/// posture for a member who is a blocked counterpart of the viewer — <c>DisplayName</c>/<c>Avatar</c>
/// are absent when true, but the row (and its <c>Status</c>) is still listed: a block never removes a
/// retained membership row (D-202).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupMemberResponse(
    Guid UserId,
    string Status,
    string InvitedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AcceptedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? Avatar,
    bool Masked)
{
    public static FriendGroupMemberResponse From(FriendGroupMemberView member) =>
        new(
            member.UserId.Value,
            member.Status,
            InstantPattern.ExtendedIso.Format(member.InvitedAt),
            member.AcceptedAt is { } acceptedAt ? InstantPattern.ExtendedIso.Format(acceptedAt) : null,
            member.DisplayName?.Value,
            member.Avatar is { } avatar ? AvatarResponse.From(avatar) : null,
            member.Masked);
}

/// <summary>
/// <see cref="Members"/> is absent for an <c>Invited</c>-only viewer (name + owner is all they may
/// read, D-199) and populated for the owner or an Accepted member. The owner's nameplate fields route
/// through the identical D-076 masking a roster row does (<see cref="OwnerMasked"/>) — always false in
/// practice, since D-202 makes an owner↔member block impossible, but not special-cased on the wire.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupDetailResponse(
    Guid Id,
    string Name,
    Guid OwnerId,
    string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OwnerDisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AvatarResponse? OwnerAvatar,
    bool OwnerMasked,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FriendGroupMemberResponse>? Members)
{
    public static FriendGroupDetailResponse From(FriendGroupDetail detail) =>
        new(
            detail.Id.Value,
            detail.Name,
            detail.OwnerId.Value,
            InstantPattern.ExtendedIso.Format(detail.CreatedAt),
            detail.OwnerDisplayName?.Value,
            detail.OwnerAvatar is { } avatar ? AvatarResponse.From(avatar) : null,
            detail.OwnerMasked,
            detail.Members?.Select(FriendGroupMemberResponse.From).ToArray());
}
