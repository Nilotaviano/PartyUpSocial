using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Polls;

/// <summary>
/// What <c>IPollRepository.LoadForUpdateAsync</c> returns once the poll parent is locked, mirroring
/// <c>SessionForUpdate</c>: the aggregate plus the sibling <c>notification_mutes</c> rows for this
/// poll, read under the same parent lock — not part of the aggregate's own child collections, but
/// needed by <c>FinalizePollUseCase</c> to carry mutes onto the created session (§12-15). A poll has
/// no reminder handoffs of its own (those attach to the session finalization creates).
/// </summary>
public sealed record PollForUpdate(Poll Poll, IReadOnlyList<NotificationMute> Mutes);

// ---- PHASE6_DESIGN.md §4 "GET /{id}": the voter-grid read -----------------------------------------

/// <summary>
/// How one registered user renders on a poll read: the display name and the avatar, resolved in the
/// same prefetch so a masking site nulls both in one expression instead of two.
/// </summary>
public sealed record UserNameplate(DisplayName? DisplayName, Avatar? Avatar);

/// <summary>
/// One roster row as the voter-grid view renders it (mirrors <c>RosterEntry</c>).
/// <see cref="MaskedForViewer"/> is set only by <see cref="PollReadUseCases"/> applying the D-076
/// blocked-pair rule — never by the repository. <see cref="ParticipationId"/> (session precedent
/// <c>RosterEntry.ParticipationId</c>) is the stable row key: the <see cref="PollParticipantId"/>
/// itself, never a user id, so a masked entry stays unkeyable-to-identity.
/// </summary>
public sealed record PollRosterEntry(
    PollParticipantId ParticipationId,
    ParticipantRef Participant,
    DisplayName? DisplayName,
    bool IsActive,
    bool MaskedForViewer = false,
    Avatar? Avatar = null);

/// <summary>
/// One candidate option with its tallies and the requesting viewer's own vote, if any.
/// <see cref="SuggestedByMasked"/> mirrors <see cref="PollRosterEntry.MaskedForViewer"/>: set only by
/// <see cref="PollReadUseCases"/> when the suggester is the roster's masked-for-viewer row -- the
/// wire contract omits the suggester's user id when it is set, matching the roster's own D-076
/// omission rather than leaking the real id through a second, unmasked channel.
/// <see cref="SuggestedByEntry"/> (D-196) is the resolved, already-masked attribution: the SAME
/// <see cref="PollRosterEntry"/> row the roster and the voter lists carry for that participant --
/// never a second masking pass -- and is null when the suggester is the poll organizer (DOMAIN_MODEL
/// §12-48: organizer-created options carry no attribution).
/// Per-category voter lists reuse the same <see cref="PollRosterEntry"/> rows the roster itself
/// carries (looked up by the vote's <see cref="ParticipantRef"/>) rather than recomputing masking:
/// a voter's <see cref="PollRosterEntry.MaskedForViewer"/> and <see cref="PollRosterEntry.ParticipationId"/>
/// are therefore identical whether they are read off the roster or off an option's voter list.
/// Ordered by <see cref="PollVote.SubmittedAt"/> ascending (first submitted response first) — the
/// vote's own write-once submission instant, cheaply available since votes are already loaded.
/// </summary>
public sealed record PollOptionGridEntry(
    PollOptionId Id,
    Instant StartTime,
    ParticipantRef SuggestedBy,
    bool SuggestedByMasked,
    PollRosterEntry? SuggestedByEntry,
    int AvailableCount,
    int MaybeCount,
    int UnavailableCount,
    VoteChoice? ViewerVote,
    IReadOnlyList<PollRosterEntry> AvailableVoters,
    IReadOnlyList<PollRosterEntry> MaybeVoters,
    IReadOnlyList<PollRosterEntry> UnavailableVoters);

/// <summary>
/// Shared by <see cref="PollReadUseCases"/> and <see cref="GuestPollReadUseCases"/> so both option
/// grids build their per-category voter lists off the identical roster rows — never a second masking
/// pass — and the identical response-time ordering.
/// </summary>
internal static class PollVoterListBuilder
{
    public static (
        IReadOnlyList<PollRosterEntry> Available,
        IReadOnlyList<PollRosterEntry> Maybe,
        IReadOnlyList<PollRosterEntry> Unavailable) Build(
        PollOptionId optionId,
        IEnumerable<PollVote> votes,
        IReadOnlyDictionary<ParticipantRef, PollRosterEntry> rosterByParticipant)
    {
        List<(PollVote Vote, PollRosterEntry Entry)> available = [];
        List<(PollVote Vote, PollRosterEntry Entry)> maybe = [];
        List<(PollVote Vote, PollRosterEntry Entry)> unavailable = [];

        foreach (PollVote vote in votes)
        {
            if (vote.OptionId != optionId)
            {
                continue;
            }

            if (!rosterByParticipant.TryGetValue(vote.Participant, out PollRosterEntry? entry))
            {
                continue;
            }

            switch (vote.Choice)
            {
                case VoteChoice.Available:
                    available.Add((vote, entry));
                    break;
                case VoteChoice.Maybe:
                    maybe.Add((vote, entry));
                    break;
                case VoteChoice.Unavailable:
                    unavailable.Add((vote, entry));
                    break;
            }
        }

        return (Order(available), Order(maybe), Order(unavailable));
    }

    private static PollRosterEntry[] Order(List<(PollVote Vote, PollRosterEntry Entry)> votes) =>
        votes.OrderBy(v => v.Vote.SubmittedAt).Select(v => v.Entry).ToArray();
}

/// <summary>The full voter-grid read (PHASE6_DESIGN.md §4).</summary>
public sealed record PollDetail(
    PollId Id,
    UserId OrganizerId,
    SessionTitle Title,
    Duration15 Duration,
    Platform Platform,
    SessionGame? Game,
    SessionNotes Notes,
    int MinAttendance,
    int? MaxCapacity,
    bool IsPrivate,
    PollDeadlineChoice DeadlineChoice,
    Instant Deadline,
    Instant SelectionWindowEndsAt,
    PollStatus Status,
    Instant CreatedAt,
    PollOptionId? SelectedOptionId,
    SessionId? CreatedSessionId,
    IReadOnlyList<PollOptionGridEntry> Options,
    IReadOnlyList<PollRosterEntry> Roster,
    // PHASE7_DESIGN.md §4: on a Finalized poll this reads through the re-parented row, resolved by
    // CreatedSessionId (Session.OriginPollId's linkage from the other side) — never a second, inert
    // poll-keyed row.
    bool Muted);

// ---- PHASE6_DESIGN.md §4.1 "GET /polls": the two-band list read ---------------------------------

/// <summary>
/// One poll as either list band renders it. <c>OptionCount</c> lets the client show "N options" on
/// the card without a second round trip; the per-option instants are not carried here -- only
/// <see cref="PollDetail"/> (a detail read) does that.
/// </summary>
public sealed record PollSummary(
    PollId Id,
    UserId OrganizerId,
    SessionTitle Title,
    PollStatus Status,
    bool IsPrivate,
    Instant Deadline,
    Instant SelectionWindowEndsAt,
    Instant CreatedAt,
    int OptionCount);

// ---- PHASE6_DESIGN.md §4 "GET /{id}/finalization-preview" ---------------------------------------

/// <summary>
/// One roster row as the preview renders it -- the shared <c>Poll.Finalize</c> allocation function's
/// own output (a created, never-persisted <c>Session</c>'s <c>Participation</c> rows), carrying a
/// resolved display name the same way <see cref="PollRosterEntry"/> does. The organizer can still be
/// blocked with a participant (D-086: masking is presentation, not removal), so this carries the same
/// D-076 <see cref="MaskedForViewer"/> flag <see cref="PollRosterEntry"/> does.
/// </summary>
public sealed record PollFinalizationPreviewRosterEntry(
    ParticipantRef Participant,
    DisplayName? DisplayName,
    ParticipationStatus Status,
    bool IsWaitlisted,
    Avatar? Avatar,
    bool MaskedForViewer = false);

/// <summary>
/// One candidate option's preview: what finalizing on this option, right now, would produce --
/// identical to the outcome <see cref="FinalizePollUseCase"/> commits, because both call the same
/// <c>Poll.Finalize</c> (design §5 "Preview guarantee").
/// </summary>
public sealed record PollFinalizationPreviewOption(
    PollOptionId OptionId,
    Instant StartTime,
    bool MeetsMinimum,
    IReadOnlyList<PollFinalizationPreviewRosterEntry> Roster);
