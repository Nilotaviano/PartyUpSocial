using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Sessions;

/// <summary>
/// D-129 defaults, D-130/D-142 bounds, D-099's invited-friend rule, and the bulk invitee/guest-link
/// bounds (PHASE4_DESIGN.md §5.2). Every invitee must already be an Accepted friend of the organizer
/// and not blocked either way — the uniform <c>user_not_found</c> failure never discloses which
/// (§8's <c>InviteSessionUsers</c> row applies the same rule at creation time).
/// </summary>
public sealed class CreateSessionUseCase(
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    ISessionNotificationMaterializer materializer,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock,
    InviteEmailService inviteEmails)
{
    public const int DefaultMinAttendance = 4;
    public const int DefaultMaxCapacity = 4;
    public const int DefaultDurationMinutes = 120;
    public const int MaxInviteeCount = 50;
    public const int MaxGuestLinkCount = 20;

    public async Task<CreateSessionResult> ExecuteAsync(
        CreateSessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<UserId> invitees = [.. (command.InviteeUserIds ?? []).Distinct()];
        int guestLinkCount = command.GuestLinkCount ?? 0;

        if (invitees.Count > MaxInviteeCount || guestLinkCount is < 0 or > MaxGuestLinkCount)
        {
            return CreateSessionResult.Failed("session_validation");
        }

        IReadOnlyList<IDomainEvent> committedEvents = [];
        CreateSessionResult result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                HashSet<UserId> lockSet = [command.OrganizerId, .. invitees];
                await sessionLocks.LockUsersAsync(lockSet, ct);

                // Bulk graph lock, one statement per table, A-068 order -- never a per-invitee loop in
                // caller-input order (PHASE4_DESIGN.md §6.1, "Bulk invite ... lock the union of ... graph
                // rows in one A-068-ordered statement, never in request-input order").
                if (invitees.Count > 0)
                {
                    IReadOnlyList<Friendship> lockedFriendships =
                        await friendships.LockPairsAsync(command.OrganizerId, invitees, ct);
                    IReadOnlyList<Block> lockedBlocks =
                        await blocks.LockPairsAsync(command.OrganizerId, invitees, ct);

                    HashSet<UserId> acceptedFriendIds =
                        [.. lockedFriendships.Where(f => f.IsAccepted).Select(f => f.Counterpart(command.OrganizerId))];
                    HashSet<UserId> blockedIds =
                        [.. lockedBlocks.Select(b => b.BlockerId == command.OrganizerId ? b.BlockedId : b.BlockerId)];

                    foreach (UserId inviteeId in invitees)
                    {
                        if (!acceptedFriendIds.Contains(inviteeId) || blockedIds.Contains(inviteeId))
                        {
                            return CreateSessionResult.Failed("user_not_found");
                        }
                    }
                }

                SessionDetails details = new(
                    SessionTitle.Create(command.Title),
                    command.StartTime,
                    Duration15.FromMinutes(command.DurationMinutes ?? DefaultDurationMinutes),
                    command.Platform ?? Platform.NotApplicable,
                    command.Game,
                    SessionNotes.Create(command.Notes),
                    command.MinAttendance ?? DefaultMinAttendance,
                    command.MaxCapacity ?? DefaultMaxCapacity,
                    command.IsPrivate ?? false ? ContentVisibility.Private : ContentVisibility.Public);

                Session session = Session.Create(command.OrganizerId, details, invitees, clock);
                sessions.Add(session);

                List<CreatedGuestLink> guestLinks = [];
                for (int i = 0; i < guestLinkCount; i++)
                {
                    (GuestLink link, string token) = session.CreateGuestLink(clock);
                    guestLinks.Add(new CreatedGuestLink(link.Id, token));
                }

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                committedEvents = events;
                await materializer.MaterializeAsync(session, events, ct);

                await reminders.ReplanAsync(session, command.OrganizerId, ct);
                foreach (UserId inviteeId in invitees)
                {
                    await reminders.ReplanAsync(session, inviteeId, ct);
                }

                return CreateSessionResult.Created(session, guestLinks);
            },
            cancellationToken);

        if (result.Succeeded && result.Session is { } createdSession)
        {
            await inviteEmails.NotifySessionEventsAsync(createdSession, committedEvents, cancellationToken);
        }

        return result;
    }
}

public sealed record CreateSessionCommand(
    UserId OrganizerId,
    string Title,
    Instant StartTime,
    int? DurationMinutes,
    Platform? Platform,
    SessionGame? Game,
    string? Notes,
    int? MinAttendance,
    int? MaxCapacity,
    bool? IsPrivate,
    IReadOnlyList<UserId>? InviteeUserIds,
    int? GuestLinkCount);

public sealed record CreatedGuestLink(GuestLinkId GuestLinkId, string Token);

public sealed record CreateSessionResult(bool Succeeded, string? Code, Session? Session, IReadOnlyList<CreatedGuestLink>? GuestLinks)
{
    public static CreateSessionResult Created(Session session, IReadOnlyList<CreatedGuestLink> guestLinks) =>
        new(true, null, session, guestLinks);

    public static CreateSessionResult Failed(string code) => new(false, code, null, null);
}
