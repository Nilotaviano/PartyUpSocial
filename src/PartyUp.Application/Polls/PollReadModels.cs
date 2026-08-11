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
/// </summary>
public sealed record PollOptionGridEntry(
    PollOptionId Id,
    Instant StartTime,
    ParticipantRef SuggestedBy,
    bool SuggestedByMasked,
    int AvailableCount,
    int MaybeCount,
    int UnavailableCount,
    VoteChoice? ViewerVote);

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
/// resolved display name the same way <see cref="PollRosterEntry"/> does. No masking is applied here:
/// the preview is organizer-only (never rendered to a blocked counterpart).
/// </summary>
public sealed record PollFinalizationPreviewRosterEntry(
    ParticipantRef Participant,
    DisplayName? DisplayName,
    ParticipationStatus Status,
    bool IsWaitlisted,
    Avatar? Avatar);

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
