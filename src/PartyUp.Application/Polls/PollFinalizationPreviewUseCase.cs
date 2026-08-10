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
/// guarantee"): open for the whole live poll -- D-187 lets the organizer select before the deadline
/// once an option is viable, so the preview that tells them which one is viable has to be readable
/// then too; it closes with the selection window (<c>now &lt; SelectionWindowEndsAt</c>). Per
/// candidate option this calls <see cref="Poll.ProjectFinalization"/> -- the exact mapping and
/// allocation <see cref="Poll.Finalize"/> commits, minus the transition -- on an untracked aggregate
/// this use case never persists (no <c>IUnitOfWork</c>, no <c>SaveChanges</c> anywhere in this file):
/// no state mutation, no notification, identical inputs give identical outputs to what
/// <see cref="FinalizePollUseCase"/> would commit right now for that option.
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

        // Round-1 fix: a terminal poll (Finalized/Expired/Canceled) has no live options loop to fall
        // through and no window to gate on -- without this check, a zero-option poll that was then
        // Canceled fell through every guard below to an empty-but-"successful" 200. Checked ahead of
        // the window gate, mirroring Poll.Finalize's own RequireStatus ordering (ApplyDueTransitions,
        // then status, then the window).
        if (poll.IsTerminal)
        {
            return PollFinalizationPreviewResult.Failed("poll_invalid_transition");
        }

        // Mirrors FinalizePollUseCase's own pre-check so a poll with zero options fails honestly
        // rather than looping zero times and returning an empty, misleadingly-"successful" preview.
        Instant now = clock.GetCurrentInstant();
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
            PollOptionProjection projection;
            try
            {
                projection = poll.ProjectFinalization(option.Id);
            }
            catch (DomainException exception)
            {
                return PollFinalizationPreviewResult.Failed(PollOptionUseCases.CodeFor(exception));
            }

            List<PollFinalizationPreviewRosterEntry> roster = [];
            foreach (FinalizedParticipation entry in projection.Roster)
            {
                DisplayName? displayName = entry.Participant.UserId is { } userId
                    ? displayNames.GetValueOrDefault(userId.Value)
                    : entry.Participant.GuestIdentityId is { } guestId
                        ? poll.GuestIdentities.FirstOrDefault(g => g.Id == guestId)?.DisplayName
                        : null;

                roster.Add(new PollFinalizationPreviewRosterEntry(
                    entry.Participant, displayName, entry.Status, entry.IsWaitlisted));
            }

            previews.Add(new PollFinalizationPreviewOption(
                projection.OptionId, projection.StartTime, projection.MeetsMinimum, roster));
        }

        return PollFinalizationPreviewResult.Ok(previews);
    }
}
