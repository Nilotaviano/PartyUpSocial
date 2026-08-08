using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Polls;

public sealed record PollFinalizationPreviewResult(
    bool Succeeded, string? Code, IReadOnlyList<PollFinalizationPreviewOption>? Options)
{
    public static PollFinalizationPreviewResult Ok(IReadOnlyList<PollFinalizationPreviewOption> options) =>
        new(true, null, options);

    public static PollFinalizationPreviewResult Failed(string code) => new(false, code, null);
}

/// <summary>
/// Organizer preview (PHASE6_DESIGN.md §4 "GET /{id}/finalization-preview", design §5 "Preview
/// guarantee"): the SAME clock-inclusive window <see cref="FinalizePollUseCase"/> gates on (due Close
/// applied first, then <c>Deadline &lt;= now &lt; SelectionWindowEndsAt</c>) -- never materialized
/// <c>Closed</c> alone (§6.1's never-delays-the-organizer promise). Per candidate option, this calls
/// the exact <see cref="Poll.Finalize"/> the real finalization commits, on a freshly loaded, untracked
/// aggregate this use case never persists (no <c>IUnitOfWork</c>, no <c>SaveChanges</c> anywhere in
/// this file) -- so the shared allocation function's output is reused verbatim: no state mutation, no
/// notification, identical inputs give identical outputs to what <see cref="FinalizePollUseCase"/>
/// would commit right now for that option.
/// </summary>
public sealed class PollFinalizationPreviewUseCase(
    IPollRepository polls,
    IPollReadRepository reads,
    IClock clock)
{
    public async Task<PollFinalizationPreviewResult> PreviewAsync(
        PollId pollId,
        UserId organizerId,
        CancellationToken cancellationToken)
    {
        Poll? poll = await polls.FindNoTrackingAsync(pollId, cancellationToken);
        if (poll is null || poll.OrganizerId != organizerId)
        {
            return PollFinalizationPreviewResult.Failed("poll_not_found");
        }

        // Mirrors FinalizePollUseCase's own pre-check split (Poll.Finalize's unconditional window
        // guard is evaluated a second time below, per simulated option, but these two outcomes are
        // reported once here so a poll with zero options still fails honestly rather than looping
        // zero times and returning an empty, misleadingly-"successful" preview.
        Instant now = clock.GetCurrentInstant();
        if (now < poll.Deadline)
        {
            return PollFinalizationPreviewResult.Failed("poll_invalid_transition");
        }

        if (now >= poll.SelectionWindowEndsAt)
        {
            return PollFinalizationPreviewResult.Failed("poll_selection_window_closed");
        }

        if (poll.Options.Count == 0 && poll.Status is PollStatus.Polling or PollStatus.Closed)
        {
            return PollFinalizationPreviewResult.Failed("poll_zero_options");
        }

        List<UserId> registeredIds = [poll.OrganizerId];
        foreach (PollParticipant participant in poll.Participants)
        {
            if (participant.UserId is { } userId)
            {
                registeredIds.Add(userId);
            }
        }

        IReadOnlyDictionary<Guid, DisplayName?> displayNames =
            await reads.FindDisplayNamesAsync(registeredIds, cancellationToken);

        List<PollFinalizationPreviewOption> previews = [];
        foreach (PollOption option in poll.Options)
        {
            Poll simulated = (await polls.FindNoTrackingAsync(pollId, cancellationToken))!;

            PollFinalizationResult result;
            try
            {
                result = simulated.Finalize(option.Id, clock);
            }
            catch (DomainException exception)
            {
                return PollFinalizationPreviewResult.Failed(PollOptionUseCases.CodeFor(exception));
            }

            List<PollFinalizationPreviewRosterEntry> roster = [];
            foreach (Participation participation in result.Session.Participations)
            {
                DisplayName? displayName = participation.UserId is { } userId
                    ? displayNames.GetValueOrDefault(userId.Value)
                    : participation.GuestIdentityId is { } guestId
                        ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                        : null;

                roster.Add(new PollFinalizationPreviewRosterEntry(
                    participation.Participant, displayName, participation.Status, participation.IsWaitlisted));
            }

            int activeGoing = roster.Count(r => !r.IsWaitlisted && r.Status == ParticipationStatus.Going);
            previews.Add(new PollFinalizationPreviewOption(
                option.Id, option.StartTime, activeGoing >= poll.MinAttendance, roster));
        }

        return PollFinalizationPreviewResult.Ok(previews);
    }
}
