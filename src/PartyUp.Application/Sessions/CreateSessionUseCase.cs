using NodaTime;
using PartyUp.Application.Abstractions;
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
    IClock clock)
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

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                HashSet<UserId> lockSet = [command.OrganizerId, .. invitees];
                await sessionLocks.LockUsersAsync(lockSet, ct);

                foreach (UserId inviteeId in invitees)
                {
                    Friendship? friendship = await friendships.FindPairForUpdateAsync(
                        command.OrganizerId, inviteeId, ct);
                    IReadOnlyList<Block> pairBlocks = await blocks.FindPairForUpdateAsync(
                        command.OrganizerId, inviteeId, ct);

                    if (friendship?.IsAccepted != true || pairBlocks.Count != 0)
                    {
                        return CreateSessionResult.Failed("user_not_found");
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
                    command.IsPrivate ?? false);

                Session session = Session.Create(command.OrganizerId, details, invitees, clock);
                sessions.Add(session);

                List<CreatedGuestLink> guestLinks = [];
                for (int i = 0; i < guestLinkCount; i++)
                {
                    (GuestLink link, string token) = session.CreateGuestLink(clock);
                    guestLinks.Add(new CreatedGuestLink(link.Id, token));
                }

                IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
                await materializer.MaterializeAsync(session, events, ct);

                await reminders.ReplanAsync(session, command.OrganizerId, ct);
                foreach (UserId inviteeId in invitees)
                {
                    await reminders.ReplanAsync(session, inviteeId, ct);
                }

                return CreateSessionResult.Created(session, guestLinks);
            },
            cancellationToken);
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
