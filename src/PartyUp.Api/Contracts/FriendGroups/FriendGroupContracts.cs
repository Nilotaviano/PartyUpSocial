using System.Text.Json.Serialization;
using NodaTime.Text;
using PartyUp.Application.FriendGroups;

namespace PartyUp.Api.Contracts.FriendGroups;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFriendGroupRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RenameFriendGroupRequest(string? Name);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InviteFriendGroupMembersRequest(IReadOnlyList<Guid> UserIds);

/// <summary>The group-level fields every viewer with any visibility may see (D-199): id, name, owner, creation time.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupResponse(Guid Id, string Name, Guid OwnerId, string CreatedAt)
{
    public static FriendGroupResponse From(FriendGroupSummary summary) =>
        new(summary.Id.Value, summary.Name, summary.OwnerId.Value, InstantPattern.ExtendedIso.Format(summary.CreatedAt));
}

/// <summary>A pending invite the actor holds in someone else's group — name + owner only (D-199).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupInviteResponse(Guid Id, string Name, Guid OwnerId, string InvitedAt)
{
    public static FriendGroupInviteResponse From(FriendGroupInviteSummary summary) =>
        new(summary.Id.Value, summary.Name, summary.OwnerId.Value, InstantPattern.ExtendedIso.Format(summary.InvitedAt));
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

/// <summary>One roster row (D-199): "invited" | "accepted" on the wire.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupMemberResponse(
    Guid UserId,
    string Status,
    string InvitedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AcceptedAt)
{
    public static FriendGroupMemberResponse From(FriendGroupMemberView member) =>
        new(
            member.UserId.Value,
            member.Status,
            InstantPattern.ExtendedIso.Format(member.InvitedAt),
            member.AcceptedAt is { } acceptedAt ? InstantPattern.ExtendedIso.Format(acceptedAt) : null);
}

/// <summary>
/// <see cref="Members"/> is absent for an <c>Invited</c>-only viewer (name + owner is all they may
/// read, D-199) and populated for the owner or an Accepted member.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FriendGroupDetailResponse(
    Guid Id,
    string Name,
    Guid OwnerId,
    string CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FriendGroupMemberResponse>? Members)
{
    public static FriendGroupDetailResponse From(FriendGroupDetail detail) =>
        new(
            detail.Id.Value,
            detail.Name,
            detail.OwnerId.Value,
            InstantPattern.ExtendedIso.Format(detail.CreatedAt),
            detail.Members?.Select(FriendGroupMemberResponse.From).ToArray());
}
