using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class UserGraphResolver(AppDbContext context) : IUserGraphResolver
{
    public async Task<ResolvedUserGraph?> ResolveSelfAsync(
        UserId viewerId,
        CancellationToken cancellationToken)
    {
        CandidateGraphRow? row = await Project(
                context.DomainUsers.AsNoTracking().Where(user => user.Id == viewerId), viewerId)
            .SingleOrDefaultAsync(cancellationToken);
        return ResolveProfile(row, viewerId);
    }

    public async Task<ResolvedUserGraph?> ResolveProfileByUsernameAsync(
        UserId viewerId, string canonicalUsername, CancellationToken cancellationToken) =>
        ResolveProfile(await FindByUsernameAsync(canonicalUsername, viewerId, cancellationToken), viewerId);

    public async Task<ResolvedUserGraph?> ResolveProfileByFriendCodeAsync(
        UserId viewerId, string canonicalFriendCode, CancellationToken cancellationToken)
    {
        FriendCode code = FriendCode.Create(canonicalFriendCode);
        CandidateGraphRow? row = await Project(
                context.DomainUsers.AsNoTracking().Where(user => user.FriendCode == code), viewerId)
            .SingleOrDefaultAsync(cancellationToken);
        return ResolveProfile(row, viewerId);
    }

    public async Task<ResolvedUserGraph?> ResolveFriendTargetByUsernameAsync(
        UserId viewerId, string canonicalUsername, CancellationToken cancellationToken)
    {
        CandidateGraphRow? row = await FindByUsernameAsync(canonicalUsername, viewerId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        Viewer viewer = ViewerFor(row, viewerId);
        return LookupVisibility.CanReceiveFriendRequest(row.Candidate, viewer)
            ? Materialize(row, ProfileVisibility.For(row.Candidate, viewer))
            : null;
    }

    public async Task<ResolvedUserGraph?> ResolveBlockTargetByUsernameAsync(
        UserId viewerId, string canonicalUsername, CancellationToken cancellationToken)
    {
        CandidateGraphRow? row = await FindByUsernameAsync(canonicalUsername, viewerId, cancellationToken);
        if (row is null || row.IsBlocked)
        {
            return null;
        }

        Viewer viewer = ViewerFor(row, viewerId);
        bool relatedSafetyException = row.RelationshipId is not null;
        if (!LookupVisibility.CanSurface(row.Candidate, viewer) &&
            !(row.Candidate.IsBanned && relatedSafetyException))
        {
            return null;
        }

        ProfileDisclosure disclosure = ProfileVisibility.For(row.Candidate, viewer);
        if (disclosure == ProfileDisclosure.Hidden && relatedSafetyException)
        {
            disclosure = row.RelationshipStatus == FriendshipStatus.Accepted
                ? ProfileDisclosure.Full
                : ProfileDisclosure.UsernameOnly;
        }

        return Materialize(row, disclosure);
    }

    public async Task<IReadOnlyDictionary<string, ResolvedUserGraph>> ResolveSteamCandidatesAsync(
        UserId viewerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(steamIds);
        if (steamIds.Count == 0)
        {
            return new Dictionary<string, ResolvedUserGraph>(StringComparer.Ordinal);
        }

        List<SteamCandidateGraphRow> rows = await (
            from identity in context.SteamIdentities.AsNoTracking()
            where steamIds.Contains(identity.SteamId64)
            join candidate in context.DomainUsers.AsNoTracking() on identity.UserId equals candidate.Id
            select new SteamCandidateGraphRow(identity.SteamId64, new CandidateGraphRow(
                candidate,
                context.Friendships.Where(friendship =>
                        (friendship.UserLoId == viewerId && friendship.UserHiId == candidate.Id) ||
                        (friendship.UserLoId == candidate.Id && friendship.UserHiId == viewerId))
                    .Select(friendship => (FriendshipId?)friendship.Id).SingleOrDefault(),
                context.Friendships.Where(friendship =>
                        (friendship.UserLoId == viewerId && friendship.UserHiId == candidate.Id) ||
                        (friendship.UserLoId == candidate.Id && friendship.UserHiId == viewerId))
                    .Select(friendship => (FriendshipStatus?)friendship.Status).SingleOrDefault(),
                context.Friendships.Where(friendship =>
                        (friendship.UserLoId == viewerId && friendship.UserHiId == candidate.Id) ||
                        (friendship.UserLoId == candidate.Id && friendship.UserHiId == viewerId))
                    .Select(friendship => (UserId?)friendship.RequestedBy).SingleOrDefault(),
                context.Blocks.Any(block =>
                    (block.BlockerId == viewerId && block.BlockedId == candidate.Id) ||
                    (block.BlockerId == candidate.Id && block.BlockedId == viewerId)))))
            .ToListAsync(cancellationToken);

        Dictionary<string, ResolvedUserGraph> visible = new(StringComparer.Ordinal);
        foreach (SteamCandidateGraphRow steamRow in rows)
        {
            CandidateGraphRow row = steamRow.Graph;
            Viewer viewer = ViewerFor(row, viewerId);
            if (LookupVisibility.CanMatchSteamImport(row.Candidate, viewer))
            {
                visible[steamRow.SteamId] = Materialize(
                    row, ProfileVisibility.For(row.Candidate, viewer));
            }
        }

        return visible;
    }

    private async Task<CandidateGraphRow?> FindByUsernameAsync(
        string canonicalUsername, UserId viewerId, CancellationToken cancellationToken)
    {
        IQueryable<User> candidates =
            from registry in context.UsernameRegistry.AsNoTracking()
            join user in context.DomainUsers.AsNoTracking() on registry.OwnerId equals (UserId?)user.Id
            where registry.State == UsernameRegistryState.Active && registry.NameCanonical == canonicalUsername
            select user;
        return await Project(candidates, viewerId).SingleOrDefaultAsync(cancellationToken);
    }

    private IQueryable<CandidateGraphRow> Project(IQueryable<User> candidates, UserId viewerId) =>
        candidates.Select(candidate => new CandidateGraphRow(
            candidate,
            context.Friendships.Where(friendship =>
                    (friendship.UserLoId == viewerId && friendship.UserHiId == candidate.Id) ||
                    (friendship.UserLoId == candidate.Id && friendship.UserHiId == viewerId))
                .Select(friendship => (FriendshipId?)friendship.Id).SingleOrDefault(),
            context.Friendships.Where(friendship =>
                    (friendship.UserLoId == viewerId && friendship.UserHiId == candidate.Id) ||
                    (friendship.UserLoId == candidate.Id && friendship.UserHiId == viewerId))
                .Select(friendship => (FriendshipStatus?)friendship.Status).SingleOrDefault(),
            context.Friendships.Where(friendship =>
                    (friendship.UserLoId == viewerId && friendship.UserHiId == candidate.Id) ||
                    (friendship.UserLoId == candidate.Id && friendship.UserHiId == viewerId))
                .Select(friendship => (UserId?)friendship.RequestedBy).SingleOrDefault(),
            context.Blocks.Any(block =>
                (block.BlockerId == viewerId && block.BlockedId == candidate.Id) ||
                (block.BlockerId == candidate.Id && block.BlockedId == viewerId))));

    private static ResolvedUserGraph? ResolveProfile(CandidateGraphRow? row, UserId viewerId)
    {
        if (row is null)
        {
            return null;
        }

        Viewer viewer = ViewerFor(row, viewerId);
        ProfileDisclosure disclosure = ProfileVisibility.For(row.Candidate, viewer);
        return disclosure == ProfileDisclosure.Hidden ? null : Materialize(row, disclosure);
    }

    private static Viewer ViewerFor(CandidateGraphRow row, UserId viewerId) =>
        Viewer.Create(
            viewerId,
            row.RelationshipStatus == FriendshipStatus.Accepted ? [row.Candidate.Id] : null,
            row.IsBlocked ? [row.Candidate.Id] : null);

    private static ResolvedUserGraph Materialize(CandidateGraphRow row, ProfileDisclosure disclosure) =>
        new(row.Candidate, disclosure, RelationshipFor(row));

    private static FriendRelationship RelationshipFor(CandidateGraphRow row)
    {
        if (row.RelationshipId is not { } id)
        {
            return new FriendRelationship(FriendRelationshipKind.None, null);
        }

        if (row.RelationshipStatus == FriendshipStatus.Accepted)
        {
            return new FriendRelationship(FriendRelationshipKind.Accepted, id);
        }

        return new FriendRelationship(
            row.RequestedBy == row.Candidate.Id
                ? FriendRelationshipKind.IncomingPending
                : FriendRelationshipKind.OutgoingPending,
            id);
    }

    private sealed record CandidateGraphRow(
        User Candidate,
        FriendshipId? RelationshipId,
        FriendshipStatus? RelationshipStatus,
        UserId? RequestedBy,
        bool IsBlocked);

    private sealed record SteamCandidateGraphRow(string SteamId, CandidateGraphRow Graph);
}
