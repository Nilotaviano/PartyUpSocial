using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Friends;

public sealed class SendFriendRequestUseCase(
    IUserGraphResolver resolver,
    IFriendPairLocker pairLocker,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task<FriendGraphCommandResult> ExecuteAsync(
        UserId actorId,
        string? targetUsername,
        CancellationToken cancellationToken)
    {
        string canonical = UsernameRegistry.Canonicalize(targetUsername);
        if (canonical.Length < Username.MinLength || canonical.Length > Username.MaxLength)
        {
            return FriendGraphCommandResult.Failed("validation_failed");
        }

        ResolvedUserGraph? self = await resolver.ResolveSelfAsync(actorId, cancellationToken);
        if (self?.User.Username is { } actorUsername &&
            string.Equals(
                UsernameRegistry.Canonicalize(actorUsername),
                canonical,
                StringComparison.Ordinal))
        {
            return FriendGraphCommandResult.Failed("friend_request_self");
        }

        ResolvedUserGraph? resolved = await resolver.ResolveFriendTargetByUsernameAsync(
            actorId,
            canonical,
            cancellationToken);
        if (resolved is null)
        {
            return FriendGraphCommandResult.Failed("user_not_found");
        }

        try
        {
            return await ExecuteLockedAsync(actorId, resolved.User.Id, canonical, cancellationToken);
        }
        catch (FriendshipPairUniqueConflictException)
        {
            return await ExecuteLockedAsync(actorId, resolved.User.Id, canonical, cancellationToken);
        }
    }

    private Task<FriendGraphCommandResult> ExecuteLockedAsync(
        UserId actorId,
        UserId targetId,
        string canonicalUsername,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                LockedFriendPair? locked = await pairLocker.LockAsync(actorId, targetId, ct);
                if (locked is null)
                {
                    return FriendGraphCommandResult.Failed("user_not_found");
                }

                User actor = locked.First.Id == actorId ? locked.First : locked.Second;
                User target = locked.First.Id == targetId ? locked.First : locked.Second;
                if (!actor.CanBeFriended ||
                    !target.CanBeFriended ||
                    target.Username is null ||
                    !string.Equals(
                        UsernameRegistry.Canonicalize(target.Username.Value),
                        canonicalUsername,
                        StringComparison.Ordinal))
                {
                    return FriendGraphCommandResult.Failed("user_not_found");
                }

                Friendship? existing = await friendships.FindPairForUpdateAsync(actorId, targetId, ct);
                IReadOnlyList<Block> pairBlocks = await blocks.FindPairForUpdateAsync(actorId, targetId, ct);
                if (pairBlocks.Count != 0)
                {
                    return FriendGraphCommandResult.Failed("user_not_found");
                }

                if (existing?.IsAccepted == true)
                {
                    return FriendGraphCommandResult.Failed("friendship_already_exists");
                }

                if (existing is not null && existing.RequestedBy == actorId)
                {
                    return FriendGraphCommandResult.Failed("friend_request_already_exists");
                }

                if (existing is not null)
                {
                    existing.Accept(actorId, clock);
                    MaterializeNotifications(existing);
                    return FriendGraphCommandResult.Completed(
                        FriendshipResource.From(existing, actorId, target));
                }

                Friendship created = Friendship.Request(actor, target, clock);
                friendships.Add(created);
                MaterializeNotifications(created);
                return FriendGraphCommandResult.CreatedRequest(
                    FriendshipResource.From(created, actorId, target));
            },
            cancellationToken);

    private void MaterializeNotifications(Friendship friendship)
    {
        foreach (IDomainEvent domainEvent in friendship.DequeueEvents())
        {
            foreach (NotificationRequest request in
                     RecipientComputer.ForFriendshipEvent(domainEvent, RecipientFilter.None))
            {
                notifications.Add(Notification.From(request, clock));
            }
        }
    }
}
