using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.FriendGroups;

/// <summary>
/// The FriendGroup command surface (D-198/D-199): create/rename/delete, invite/accept/decline/revoke,
/// remove/leave. Every command is wrapped in <see cref="IUnitOfWork.ExecuteAsync{T}"/>, mirrors
/// <c>SessionParticipationUseCases</c>'s gate-then-mutate-then-materialize shape, and fails closed —
/// a group the actor does not <see cref="FriendGroup.Involves"/> is reported identically to a group
/// that does not exist (<c>"group_not_found"</c>). Guard order and cap enforcement inside the
/// aggregate itself (Forbidden/NotFound/Duplicate/LimitExceeded) are left to bubble as
/// <see cref="Domain.Kernel.DomainException"/> — the API's <c>DomainExceptionMapper</c> renders them,
/// the same way <c>SessionParticipationUseCases</c> lets <c>Session</c>'s own domain exceptions
/// through untouched. Only the codes this use case alone can compute (fail-closed visibility, the
/// owned-group cap, the per-owner name conflict, the per-group member cap) get an app-level code here.
/// </summary>
public sealed class FriendGroupUseCases(
    IFriendGroupRepository groups,
    IUserRepository users,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    INotificationRepository notifications,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    /// <summary>D-208: groups owned per user, the owner's own <c>users</c> row locked before counting.</summary>
    public const int MaxOwnedGroups = 50;

    public async Task<FriendGroupCommandResult> CreateAsync(
        UserId actorId,
        string? name,
        CancellationToken cancellationToken)
    {
        GroupName groupName;
        try
        {
            groupName = GroupName.Create(name);
        }
        catch (DomainException)
        {
            return FriendGroupCommandResult.Failed("validation_failed");
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    if (await users.FindForUpdateAsync(actorId, ct) is null)
                    {
                        return FriendGroupCommandResult.Failed("user_not_found");
                    }

                    int owned = await groups.CountOwnedForUpdateAsync(actorId, ct);
                    if (owned >= MaxOwnedGroups)
                    {
                        return FriendGroupCommandResult.Failed("group_limit_reached");
                    }

                    FriendGroup group = FriendGroup.Create(actorId, groupName, clock);
                    groups.Add(group);

                    return FriendGroupCommandResult.Ok(FriendGroupSummary.From(group));
                },
                cancellationToken);
        }
        catch (FriendGroupNameConflictException)
        {
            return FriendGroupCommandResult.Failed("group_name_taken");
        }
    }

    public async Task<FriendGroupCommandResult> RenameAsync(
        UserId actorId,
        FriendGroupId groupId,
        string? name,
        CancellationToken cancellationToken)
    {
        GroupName groupName;
        try
        {
            groupName = GroupName.Create(name);
        }
        catch (DomainException)
        {
            return FriendGroupCommandResult.Failed("validation_failed");
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                    if (group is null || !group.Involves(actorId))
                    {
                        return FriendGroupCommandResult.Failed("group_not_found");
                    }

                    group.Rename(actorId, groupName);

                    return FriendGroupCommandResult.Ok(FriendGroupSummary.From(group));
                },
                cancellationToken);
        }
        catch (FriendGroupNameConflictException)
        {
            return FriendGroupCommandResult.Failed("group_name_taken");
        }
    }

    /// <summary>
    /// Owner-only hard delete (D-199). Before <see cref="IFriendGroupRepository.Remove"/>, reads every
    /// session/poll organizer whose audience currently targets this group and writes them each one
    /// <c>AudienceGroupDeleted</c> row in the same transaction (D-205/D-207) — the junction rows
    /// themselves cascade-delete with the group (D-205's zero-group degenerate state).
    /// </summary>
    public Task<FriendGroupCommandResult> DeleteAsync(
        UserId actorId,
        FriendGroupId groupId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                if (group.OwnerId != actorId)
                {
                    throw new DomainException(DomainError.Forbidden, "Only the group's owner can do this.");
                }

                IReadOnlyCollection<UserId> organizerIds =
                    await groups.ListDistinctOrganizersTargetingAsync(groupId, ct);
                foreach (UserId organizerId in organizerIds)
                {
                    // The deleting owner already knows they deleted their own group — only the other
                    // organizers whose audience it named need telling.
                    if (organizerId == actorId)
                    {
                        continue;
                    }

                    notifications.Add(Notification.Create(
                        organizerId,
                        NotificationType.AudienceGroupDeleted,
                        NotificationSubject.ForFriendGroupDeleted(groupId),
                        clock));
                }

                groups.Remove(group);

                return FriendGroupCommandResult.Ok();
            },
            cancellationToken);

    /// <summary>
    /// D-201/D-202: per invitee, resolves the friendship + block state between the *owner* and the
    /// invitee (targeting authority belongs to the owner, not the inviting actor — only the owner can
    /// invite, <see cref="FriendGroup.Invite"/>'s <c>RequireOwner</c> guard — but the friendship gate is
    /// the owner's graph) and collapses both "not a friend" and "blocked" into the single
    /// <c>isAcceptedFriendOfOwner: false</c> input the aggregate turns into one indistinguishable
    /// <c>NotFound</c> (D-202 — no block-detection oracle).
    /// </summary>
    /// <remarks>
    /// The whole batch is pre-validated against <see cref="FriendGroup.MaxMembers"/> before a single
    /// <see cref="FriendGroup.Invite"/> call mutates anything: <see cref="IUnitOfWork.ExecuteAsync{T}"/>
    /// commits whatever the delegate leaves behind when it returns normally, so a mid-loop
    /// <c>Failed("group_member_limit_reached")</c> after some invitees had already been added would
    /// otherwise commit that partial roster with no notification for any of it. A mid-loop aggregate
    /// exception (Duplicate/NotFound/etc.) is not similarly pre-checked — it propagates out of the
    /// delegate and rolls the whole transaction back, which is the correct outcome for those.
    /// </remarks>
    public Task<FriendGroupCommandResult> InviteAsync(
        UserId actorId,
        FriendGroupId groupId,
        IReadOnlyCollection<UserId> inviteeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inviteeIds);

        List<UserId> invitees = [.. inviteeIds.Distinct()];
        if (invitees.Count == 0)
        {
            return Task.FromResult(FriendGroupCommandResult.Failed("validation_failed"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                Dictionary<UserId, bool> isAcceptedFriendOfOwner = new(invitees.Count);
                int newRowCount = 0;
                foreach (UserId inviteeId in invitees)
                {
                    Friendship? friendship = await friendships.FindPairAsync(group.OwnerId, inviteeId, ct);
                    IReadOnlyList<Block> pairBlocks = await blocks.FindPairAsync(group.OwnerId, inviteeId, ct);
                    bool isFriend = friendship is { IsAccepted: true } && pairBlocks.Count == 0;
                    isAcceptedFriendOfOwner[inviteeId] = isFriend;

                    // Only count invitees that would actually add a new row: an already-present member
                    // or the owner themself fails before the aggregate ever reaches its cap check.
                    if (isFriend
                        && inviteeId != group.OwnerId
                        && !group.Members.Any(member => member.UserId == inviteeId))
                    {
                        newRowCount++;
                    }
                }

                if (group.Members.Count + newRowCount > FriendGroup.MaxMembers)
                {
                    return FriendGroupCommandResult.Failed("group_member_limit_reached");
                }

                foreach (UserId inviteeId in invitees)
                {
                    group.Invite(actorId, inviteeId, isAcceptedFriendOfOwner[inviteeId], clock);
                }

                MaterializeNotifications(group);

                return FriendGroupCommandResult.Ok(FriendGroupSummary.From(group));
            },
            cancellationToken);
    }

    public Task<FriendGroupCommandResult> AcceptInviteAsync(
        UserId actorId,
        FriendGroupId groupId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                group.AcceptInvite(actorId, clock);
                MaterializeNotifications(group);

                return FriendGroupCommandResult.Ok(FriendGroupSummary.From(group));
            },
            cancellationToken);

    /// <summary>D-207: silent — no notification.</summary>
    public Task<FriendGroupCommandResult> DeclineInviteAsync(
        UserId actorId,
        FriendGroupId groupId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                group.DeclineInvite(actorId);

                return FriendGroupCommandResult.Ok();
            },
            cancellationToken);

    /// <summary>D-207: silent — no notification.</summary>
    public Task<FriendGroupCommandResult> RevokeInviteAsync(
        UserId actorId,
        FriendGroupId groupId,
        UserId inviteeId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                group.RevokeInvite(actorId, inviteeId);

                return FriendGroupCommandResult.Ok();
            },
            cancellationToken);

    /// <summary>D-207: silent — no notification.</summary>
    public Task<FriendGroupCommandResult> RemoveMemberAsync(
        UserId actorId,
        FriendGroupId groupId,
        UserId memberId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                group.RemoveMember(actorId, memberId);

                return FriendGroupCommandResult.Ok();
            },
            cancellationToken);

    /// <summary>D-207: silent — no notification.</summary>
    public Task<FriendGroupCommandResult> LeaveAsync(
        UserId actorId,
        FriendGroupId groupId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                FriendGroup? group = await groups.LoadForUpdateAsync(groupId, ct);
                if (group is null || !group.Involves(actorId))
                {
                    return FriendGroupCommandResult.Failed("group_not_found");
                }

                group.Leave(actorId);

                return FriendGroupCommandResult.Ok();
            },
            cancellationToken);

    private void MaterializeNotifications(FriendGroup group)
    {
        foreach (IDomainEvent domainEvent in group.DequeueEvents())
        {
            foreach (NotificationRequest request in
                     RecipientComputer.ForFriendGroupEvent(domainEvent, RecipientFilter.None))
            {
                notifications.Add(Notification.From(request, clock));
            }
        }
    }
}
