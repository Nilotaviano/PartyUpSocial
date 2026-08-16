using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Notifications;

/// <summary>
/// The notification-actor read projection (PHASE7_DESIGN.md §2): consumes the shipped
/// <see cref="IdentityMasking"/> — DOMAIN_MODEL §7's one rendering service — rather than
/// reimplementing it, and re-resolves each row's subject through the same visibility engine path
/// <see cref="Sessions.SessionReadUseCases"/> and <see cref="Polls.PollReadUseCases"/> already drive,
/// because a row whose subject the recipient can no longer reach (blocked pair, departed roster,
/// lapsed inherited access — §7 rule 4 and its D-076 sibling) must not leak the subject's current
/// title or a deep link. <see cref="ProjectAsync"/> is per-row and touches no HTTP context, so S3's
/// dispatch job can call it at send time and get the same reachability answer the center renders.
/// A friend-group ref (D-207) resolves through <see cref="FriendGroup.Involves"/> instead — no
/// visibility engine involvement, group access is the literal roster — and, unlike the session/poll
/// refs, never forces the whole row to <see cref="Minimal"/>: the group's name is masked but the row
/// (actor, other fields) still renders, because an <c>AudienceGroupDeleted</c> ref dangles by
/// construction (the group is hard-deleted in the same transaction that raises the row) and must not
/// suppress the notification itself.
/// </summary>
public sealed class NotificationReadUseCases(
    ISessionRepository sessions,
    IPollRepository polls,
    IUserRepository users,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IFriendGroupRepository groups)
{
    /// <summary>Resolves one notification row for the recipient who owns it (the only legal viewer, D-086).</summary>
    public async Task<NotificationReadModel> ProjectAsync(
        Notification notification,
        UserId recipientId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        User? recipient = await users.FindAsync(recipientId, cancellationToken);
        (string locale, DateTimeZone zone) = ResolveRecipientRendering(recipient);

        bool hasSubjectRef = notification.SessionId is not null || notification.PollId is not null;

        // Both refs load unconditionally (PollOptionSelected (19) carries both, §6.4-8) so the poll
        // fallback below has what it needs without a second round trip.
        Session? session = notification.SessionId is { } sessionId
            ? await sessions.FindAsync(sessionId, cancellationToken)
            : null;
        Poll? poll = notification.PollId is { } pollId
            ? await polls.FindNoTrackingAsync(pollId, cancellationToken)
            : null;
        FriendGroup? group = notification.FriendGroupId is { } groupId
            ? await groups.FindNoTrackingAsync(groupId, cancellationToken)
            : null;

        HashSet<UserId> counterparts = [];
        AddRosterCounterparts(counterparts, session);
        AddRosterCounterparts(counterparts, poll);

        if (notification.ActorUserId is { } actorRefId)
        {
            counterparts.Add(actorRefId);
        }

        counterparts.Remove(recipientId);

        Viewer viewer = await BuildViewerAsync(recipientId, counterparts, cancellationToken);

        // The session ref is the primary target; a both-ref row falls back to the poll only when the
        // session is unreachable — e.g. a voter who is Unavailable on the winning option is dropped
        // from the finalized session's roster (§6.4-3) but remains a deliberate poll recipient
        // (RecipientComputer's PollAudience) as long as they still hold poll access. Fail closed: a
        // subject ref with no aggregate found is unreachable, same as one the engine refuses outright.
        bool sessionReachable = session is not null && VisibilityEngine.ResolveSession(session, viewer).IsVisible;
        bool pollReachable = poll is not null && VisibilityEngine.ResolvePoll(poll, viewer).IsVisible;
        // D-207: literal group membership, not the visibility engine — and, deliberately, never part of
        // `hasSubjectRef` above, so an unresolved group only masks the subject fields below rather than
        // collapsing the whole row to Minimal (AudienceGroupDeleted must still notify).
        bool groupReachable = group is not null && group.Involves(recipientId);

        NotificationSubjectKind kind = notification.SessionId is not null && sessionReachable
            ? NotificationSubjectKind.Session
            : notification.PollId is not null && pollReachable
                ? NotificationSubjectKind.Poll
                : notification.FriendGroupId is not null && groupReachable
                    ? NotificationSubjectKind.Group
                    : NotificationSubjectKind.None;

        bool reachable = !hasSubjectRef || kind != NotificationSubjectKind.None;

        MaskedIdentity? actor = null;
        Guid? actorUserId = null;
        if (reachable && notification.ActorUserId is { } actorId)
        {
            User? actorUser = await users.FindAsync(actorId, cancellationToken);
            if (actorUser is not null)
            {
                actor = IdentityMasking.ForUser(actorUser, viewer);

                // PHASE7_DESIGN.md §4 wire rule: a blocked-pair-masked actor omits actorUserId too;
                // named, tombstoned and profile-incomplete all keep it.
                actorUserId = actor.Value.Reason == MaskingReason.Blocked ? null : actorId.Value;
            }
        }

        if (!reachable)
        {
            return Minimal(notification, locale, zone);
        }

        Guid? subjectId = kind switch
        {
            NotificationSubjectKind.Session => session!.Id.Value,
            NotificationSubjectKind.Poll => poll!.Id.Value,
            NotificationSubjectKind.Group => group!.Id.Value,
            _ => null,
        };

        string? subjectTitle = kind switch
        {
            NotificationSubjectKind.Session => session!.Title.Value,
            NotificationSubjectKind.Poll => poll!.Title.Value,
            NotificationSubjectKind.Group => group!.Name.Value,
            _ => null,
        };

        Instant? optionStartTime =
            notification.Type is NotificationType.PollOptionSuggested or NotificationType.PollOptionSelected
                ? notification.OptionStartTime
                : null;

        return new NotificationReadModel(
            notification.Id,
            notification.Type,
            notification.CreatedAt,
            notification.ReadAt,
            actor,
            actorUserId,
            kind,
            subjectId,
            subjectTitle,
            optionStartTime,
            locale,
            zone);
    }

    /// <summary>
    /// Whether <paramref name="notification"/>'s subject is still reachable for its recipient — the
    /// same gate <see cref="ProjectAsync"/> renders through, exposed so S3's dispatch job can decide
    /// <c>Skipped</c> without re-deriving the answer.
    /// </summary>
    public async Task<bool> IsSubjectReachableAsync(
        Notification notification,
        UserId recipientId,
        CancellationToken cancellationToken)
    {
        NotificationReadModel row = await ProjectAsync(notification, recipientId, cancellationToken);
        return row.HasSubject || (notification.SessionId is null && notification.PollId is null);
    }

    public async Task<IReadOnlyList<NotificationReadModel>> ProjectPageAsync(
        IReadOnlyList<Notification> page,
        UserId recipientId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        List<NotificationReadModel> results = new(page.Count);
        foreach (Notification notification in page)
        {
            results.Add(await ProjectAsync(notification, recipientId, cancellationToken));
        }

        return results;
    }

    private static NotificationReadModel Minimal(Notification notification, string locale, DateTimeZone zone) =>
        new(
            notification.Id,
            notification.Type,
            notification.CreatedAt,
            notification.ReadAt,
            null,
            null,
            NotificationSubjectKind.None,
            null,
            null,
            null,
            locale,
            zone);

    private static void AddRosterCounterparts(HashSet<UserId> counterparts, Session? session)
    {
        if (session is null)
        {
            return;
        }

        counterparts.Add(session.OrganizerId);
        foreach (Participation participation in session.Participations)
        {
            if (participation.UserId is { } userId)
            {
                counterparts.Add(userId);
            }
        }
    }

    private static void AddRosterCounterparts(HashSet<UserId> counterparts, Poll? poll)
    {
        if (poll is null)
        {
            return;
        }

        counterparts.Add(poll.OrganizerId);
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId)
            {
                counterparts.Add(userId);
            }
        }
    }

    /// <summary>D-060/spec §8.5: instants render in the recipient's own zone; a user with neither set falls back to en/UTC.</summary>
    private static (string Locale, DateTimeZone Zone) ResolveRecipientRendering(User? recipient)
    {
        string? localeValue = recipient?.Locale is { } locale ? locale.Value : null;
        DateTimeZone zone = recipient?.TimeZone ?? DateTimeZone.Utc;

        return (EmbeddedEmailTemplateRenderer.CanonicalizeLocale(localeValue), zone);
    }

    /// <summary>Mirrors <c>SessionReadUseCases.BuildViewerAsync</c>/<c>PollReadUseCases.BuildViewerAsync</c>: no row lock, a GET.</summary>
    private async Task<Viewer> BuildViewerAsync(
        UserId viewerId,
        HashSet<UserId> counterparts,
        CancellationToken cancellationToken)
    {
        if (counterparts.Count == 0)
        {
            return Viewer.Create(viewerId);
        }

        IReadOnlyList<Friendship> pairFriendships =
            await friendships.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);
        IReadOnlyList<Block> pairBlocks =
            await blocks.FindPairsAmongAsync(viewerId, counterparts, cancellationToken);

        List<UserId> friendIds = [.. pairFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(viewerId))];
        List<UserId> blockedIds = [.. pairBlocks.Select(b => b.BlockerId == viewerId ? b.BlockedId : b.BlockerId)];

        return Viewer.Create(viewerId, friendIds, blockedIds);
    }
}
