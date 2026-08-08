using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Polls;

/// <summary>
/// Poll creation (spec §7.3, PHASE6_DESIGN.md §2 "Create"): ≥2 distinct options after dedupe and the
/// 12-cap (D-080/D-131, aggregate-enforced), instants ≤ creation+1y (§12-11, aggregate-enforced),
/// deadline ∈ {6,12,24,48} default 24 (D-070/D-131), a non-private poll needs ≥1 directly invited
/// registered friend (D-125) — checked here exactly like <see cref="CreateSessionUseCase"/> checks
/// invitee eligibility: all-or-nothing, so a single unusable invitee never partially attaches and
/// never discloses which invitee was blocked (uniform <c>user_not_found</c>). A private poll may be
/// created with zero registered invitees and guest links minted in the same transaction (spec §7.3).
/// </summary>
public sealed class CreatePollUseCase(
    IPollRepository polls,
    IPollLockRepository pollLocks,
    IFriendshipRepository friendships,
    IBlockRepository blocks,
    IPollNotificationMaterializer materializer,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    public const int DefaultMinAttendance = 4;
    public const int MaxInviteeCount = 50;
    public const int MaxGuestLinkCount = 20;
    public const PollDeadlineChoice DefaultDeadlineChoice = PollDeadlineChoice.Hours24;

    public async Task<CreatePollResult> ExecuteAsync(
        CreatePollCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<UserId> invitees = [.. (command.InviteeUserIds ?? []).Distinct()];
        int guestLinkCount = command.GuestLinkCount ?? 0;

        if (invitees.Count > MaxInviteeCount || guestLinkCount is < 0 or > MaxGuestLinkCount)
        {
            return CreatePollResult.Failed("poll_validation");
        }

        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                // P3-LK-01: users -> friendships -> blocks -> ... -> poll parents. The union of
                // organizer + invitees is locked in one A-068-ordered statement, never a per-invitee
                // loop, mirroring CreateSessionUseCase.
                HashSet<UserId> lockSet = [command.OrganizerId, .. invitees];
                await pollLocks.LockUsersAsync(lockSet, ct);

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
                            return CreatePollResult.Failed("user_not_found");
                        }
                    }
                }

                PollDetails details = new(
                    SessionTitle.Create(command.Title),
                    Duration15.FromMinutes(command.DurationMinutes ?? CreateSessionUseCase.DefaultDurationMinutes),
                    command.Platform ?? Platform.NotApplicable,
                    command.Game,
                    SessionNotes.Create(command.Notes),
                    command.MinAttendance ?? DefaultMinAttendance,
                    command.MaxCapacity,
                    command.IsPrivate ?? false);

                Poll poll;
                try
                {
                    poll = Poll.Create(
                        command.OrganizerId,
                        details,
                        command.DeadlineChoice ?? DefaultDeadlineChoice,
                        command.OptionStartTimes ?? [],
                        invitees,
                        clock);
                }
                catch (DomainException exception)
                {
                    return CreatePollResult.Failed(exception.Error switch
                    {
                        DomainError.LimitExceeded => "poll_option_cap",
                        DomainError.ValidationFailed => "poll_validation",
                        _ => "poll_validation",
                    });
                }

                polls.Add(poll);

                List<CreatedGuestLink> guestLinks = [];
                for (int i = 0; i < guestLinkCount; i++)
                {
                    (GuestLink link, string token) = poll.CreateGuestLink(clock);
                    guestLinks.Add(new CreatedGuestLink(link.Id, token));
                }

                IReadOnlyList<IDomainEvent> events = poll.DequeueEvents();
                await materializer.MaterializeAsync(poll, events, ct);

                return CreatePollResult.Created(poll, guestLinks);
            },
            cancellationToken);
    }
}

public sealed record CreatePollCommand(
    UserId OrganizerId,
    string Title,
    int? DurationMinutes,
    Platform? Platform,
    SessionGame? Game,
    string? Notes,
    int? MinAttendance,
    int? MaxCapacity,
    bool? IsPrivate,
    IReadOnlyList<Instant>? OptionStartTimes,
    PollDeadlineChoice? DeadlineChoice,
    IReadOnlyList<UserId>? InviteeUserIds,
    int? GuestLinkCount);

public sealed record CreatePollResult(bool Succeeded, string? Code, Poll? Poll, IReadOnlyList<CreatedGuestLink>? GuestLinks)
{
    public static CreatePollResult Created(Poll poll, IReadOnlyList<CreatedGuestLink> guestLinks) =>
        new(true, null, poll, guestLinks);

    public static CreatePollResult Failed(string code) => new(false, code, null, null);
}
