using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Friends;

public sealed class ImportSteamFriendsUseCase(
    ISteamFriendListClient steamFriends,
    ISteamImportAccountGateway accounts,
    IUserRepository users,
    IPublicLinkBuilder links,
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<ImportSteamFriendsResult> ExecuteAsync(
        UserId importerId,
        CancellationToken cancellationToken)
    {
        User? importer = await users.FindAsync(importerId, cancellationToken);
        if (importer?.FriendCode is not { } friendCode)
        {
            return SteamImportNotLinked.Instance;
        }

        string invitationUrl = links.Add(friendCode);
        string? steamId64 = await accounts.FindSteamIdAsync(importerId, cancellationToken);
        if (steamId64 is null)
        {
            return SteamImportNotLinked.Instance;
        }

        while (true)
        {
            SteamFriendListResult provider = await steamFriends.GetAsync(
                steamId64,
                cancellationToken);
            if (provider is not SteamFriendListSuccess success)
            {
                return ProviderResult(provider, invitationUrl);
            }

            SteamFriend[] imported = success.Friends
                .Where(friend => !string.IsNullOrWhiteSpace(friend.SteamId64))
                .DistinctBy(friend => friend.SteamId64, StringComparer.Ordinal)
                .Where(friend => !string.Equals(
                    friend.SteamId64,
                    steamId64,
                    StringComparison.Ordinal))
                .ToArray();
            string[] importedIds = imported.Select(friend => friend.SteamId64).ToArray();
            while (true)
            {
                IReadOnlyDictionary<string, UserId> projectedOwners =
                    await accounts.FindSteamIdentityOwnersAsync(importedIds, cancellationToken);
                try
                {
                    return await unitOfWork.ExecuteAsync(
                        async ct => await MatchAsync(
                            importerId,
                            steamId64,
                            imported,
                            importedIds,
                            projectedOwners,
                            invitationUrl,
                            ct),
                        cancellationToken);
                }
                catch (CandidateLockSetChangedException)
                {
                }
                catch (ImporterSteamIdentityChangedException changed)
                {
                    steamId64 = changed.SteamId64;
                    break;
                }
            }
        }
    }

    private async Task<ImportSteamFriendsResult> MatchAsync(
        UserId importerId,
        string requestedSteamId64,
        SteamFriend[] imported,
        IReadOnlyCollection<string> importedIds,
        IReadOnlyDictionary<string, UserId> projectedOwners,
        string invitationUrl,
        CancellationToken cancellationToken)
    {
        UserId[] lockSet = projectedOwners.Values
            .Append(importerId)
            .Distinct()
            .OrderBy(id => id.Value)
            .ToArray();
        await accounts.LockUsersAsync(lockSet, cancellationToken);
        SteamImportOwnershipSnapshot snapshot =
            await accounts.ReadSteamImportOwnershipSnapshotAsync(
                importerId,
                importedIds,
                cancellationToken);

        if (snapshot.ImporterSteamId64 is null)
        {
            return SteamImportNotLinked.Instance;
        }

        if (!string.Equals(
                snapshot.ImporterSteamId64,
                requestedSteamId64,
                StringComparison.Ordinal))
        {
            throw new ImporterSteamIdentityChangedException(snapshot.ImporterSteamId64);
        }

        UserId[] stableLockSet = snapshot.Candidates.Values
            .Select(candidate => candidate.User.Id)
            .Append(importerId)
            .Distinct()
            .OrderBy(id => id.Value)
            .ToArray();
        if (!stableLockSet.SequenceEqual(lockSet))
        {
            throw new CandidateLockSetChangedException();
        }

        if (imported.Length == 0)
        {
            return new SteamImportZeroFriends([], [], invitationUrl);
        }

        var matches = new List<SteamMatchedUser>();
        var unmatched = new List<SteamUnmatchedFriend>();
        foreach (SteamFriend friend in imported)
        {
            if (!snapshot.Candidates.TryGetValue(friend.SteamId64, out SteamImportCandidate? candidate))
            {
                unmatched.Add(Unmatched(friend, invitationUrl));
                continue;
            }

            Viewer viewer = Viewer.Create(
                importerId,
                candidate.Relationship == FriendRelationshipKind.Accepted
                    ? [candidate.User.Id]
                    : null,
                candidate.IsBlocked ? [candidate.User.Id] : null);
            if (!LookupVisibility.CanMatchSteamImport(candidate.User, viewer))
            {
                unmatched.Add(Unmatched(friend, invitationUrl));
                continue;
            }

            if (candidate.Relationship != FriendRelationshipKind.None)
            {
                continue;
            }

            string username = candidate.User.Username?.Value
                ?? throw new InvalidOperationException("A visible Steam match must have a username.");
            Avatar avatar = candidate.User.Avatar
                ?? throw new InvalidOperationException("A visible Steam match must have an avatar.");
            matches.Add(new SteamMatchedUser(username, avatar));
            IReadOnlyList<NotificationRequest> requests = RecipientComputer.SteamImportMatchAtImport(
                importerId,
                candidate.User.Id,
                RecipientFilter.None);
            foreach (NotificationRequest request in requests)
            {
                await notifications.AddSteamMatchIfAbsentAsync(
                    Notification.From(request, clock),
                    cancellationToken);
            }
        }

        return matches.Count > 0
            ? new SteamImportReady(matches, unmatched, invitationUrl)
            : new SteamImportZeroMatches([], unmatched, invitationUrl);
    }

    private static ImportSteamFriendsResult ProviderResult(
        SteamFriendListResult result,
        string invitationUrl) => result switch
        {
            SteamFriendListNotConfigured => new SteamImportNotConfigured(invitationUrl),
            SteamFriendListPrivateProfile => new SteamImportPrivateProfile([], [], invitationUrl),
            SteamFriendListUpstreamFailure => new SteamImportUpstreamFailure(true, invitationUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

    private static SteamUnmatchedFriend Unmatched(SteamFriend friend, string invitationUrl) =>
        new(
            friend.SteamId64,
            friend.PersonaName,
            friend.AvatarUrl,
            invitationUrl);

    private sealed class CandidateLockSetChangedException : Exception;

    private sealed class ImporterSteamIdentityChangedException(string steamId64) : Exception
    {
        internal string SteamId64 { get; } = steamId64;
    }
}

public abstract record ImportSteamFriendsResult;

public sealed record SteamImportNotLinked : ImportSteamFriendsResult
{
    public static SteamImportNotLinked Instance { get; } = new();
}

public sealed record SteamImportReady(
    IReadOnlyList<SteamMatchedUser> Matches,
    IReadOnlyList<SteamUnmatchedFriend> Unmatched,
    string InvitationUrl) : ImportSteamFriendsResult;

public sealed record SteamImportPrivateProfile(
    IReadOnlyList<SteamMatchedUser> Matches,
    IReadOnlyList<SteamUnmatchedFriend> Unmatched,
    string InvitationUrl) : ImportSteamFriendsResult;

public sealed record SteamImportZeroFriends(
    IReadOnlyList<SteamMatchedUser> Matches,
    IReadOnlyList<SteamUnmatchedFriend> Unmatched,
    string InvitationUrl) : ImportSteamFriendsResult;

public sealed record SteamImportZeroMatches(
    IReadOnlyList<SteamMatchedUser> Matches,
    IReadOnlyList<SteamUnmatchedFriend> Unmatched,
    string InvitationUrl) : ImportSteamFriendsResult;

public sealed record SteamImportNotConfigured(
    string InvitationUrl) : ImportSteamFriendsResult;

public sealed record SteamImportUpstreamFailure(
    bool Retryable,
    string InvitationUrl) : ImportSteamFriendsResult;

public sealed record SteamMatchedUser(string Username, Avatar Avatar);

public sealed record SteamUnmatchedFriend(
    string SteamId64,
    string? PersonaName,
    string? AvatarUrl,
    string InvitationUrl);
