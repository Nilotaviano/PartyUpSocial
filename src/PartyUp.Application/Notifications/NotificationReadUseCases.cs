using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
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
/// </summary>
public sealed class NotificationReadUseCases(
    ISessionRepository sessions,
    IPollRepository polls,
    IUserRepository users,
    IFriendshipRepository friendships,
    IBlockRepository blocks)
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

        Session? session = null;
        Poll? poll = null;
        NotificationSubjectKind kind = NotificationSubjectKind.None;

        // PollOptionSelected (19) carries both refs (§6.4-8): the session finalization minted is the
        // live, reachable target after finalization, so a session ref wins when both are present.
        if (notification.SessionId is { } sessionId)
        {
            session = await sessions.FindAsync(sessionId, cancellationToken);
            kind = NotificationSubjectKind.Session;
        }
        else if (notification.PollId is { } pollId)
        {
            poll = await polls.FindNoTrackingAsync(pollId, cancellationToken);
            kind = NotificationSubjectKind.Poll;
        }

        HashSet<UserId> counterparts = [];
        if (session is not null)
        {
            counterparts.Add(session.OrganizerId);
            foreach (Participation participation in session.Participations)
            {
                if (participation.UserId is { } userId)
                {
                    counterparts.Add(userId);
                }
            }
        }
        else if (poll is not null)
        {
            counterparts.Add(poll.OrganizerId);
            foreach (PollParticipant participant in poll.Participants)
            {
                if (participant.UserId is { } userId)
                {
                    counterparts.Add(userId);
                }
            }
        }

        if (notification.ActorUserId is { } actorRefId)
        {
            counterparts.Add(actorRefId);
        }

        counterparts.Remove(recipientId);

        Viewer viewer = await BuildViewerAsync(recipientId, counterparts, cancellationToken);

        // Fail closed: a subject ref with no aggregate found (neither session nor poll loaded) is
        // unreachable, same as one the visibility engine explicitly refuses.
        bool reachable = !hasSubjectRef
            || (session is not null && VisibilityEngine.ResolveSession(session, viewer).IsVisible)
            || (poll is not null && VisibilityEngine.ResolvePoll(poll, viewer).IsVisible);

        MaskedIdentity? actor = null;
        if (reachable && notification.ActorUserId is { } actorId)
        {
            User? actorUser = await users.FindAsync(actorId, cancellationToken);
            actor = actorUser is not null ? IdentityMasking.ForUser(actorUser, viewer) : null;
        }

        if (!reachable)
        {
            return Minimal(notification, locale, zone);
        }

        Guid? subjectId = kind switch
        {
            NotificationSubjectKind.Session => session!.Id.Value,
            NotificationSubjectKind.Poll => poll!.Id.Value,
            _ => null,
        };

        SessionTitle? subjectTitle = kind switch
        {
            NotificationSubjectKind.Session => session!.Title,
            NotificationSubjectKind.Poll => poll!.Title,
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
            hasSubjectRef ? kind : NotificationSubjectKind.None,
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
            NotificationSubjectKind.None,
            null,
            null,
            null,
            locale,
            zone);

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
