using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Abstractions;

public interface IUserGraphResolver
{
    Task<ResolvedUserGraph?> ResolveSelfAsync(
        UserId viewerId,
        CancellationToken cancellationToken);

    Task<ResolvedUserGraph?> ResolveProfileByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken);

    Task<ResolvedUserGraph?> ResolveProfileByFriendCodeAsync(
        UserId viewerId,
        string canonicalFriendCode,
        CancellationToken cancellationToken);

    Task<ResolvedUserGraph?> ResolveFriendTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken);

    Task<ResolvedUserGraph?> ResolveBlockTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken);
}

public enum FriendRelationshipKind
{
    None = 0,
    IncomingPending = 1,
    OutgoingPending = 2,
    Accepted = 3,
}

public sealed record FriendRelationship(
    FriendRelationshipKind Kind,
    FriendshipId? FriendshipId);

public sealed record ResolvedUserGraph(
    User User,
    ProfileDisclosure Disclosure,
    FriendRelationship Relationship);
