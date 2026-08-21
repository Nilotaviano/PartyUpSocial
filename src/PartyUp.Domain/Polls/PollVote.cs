using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Polls;

/// <summary>
/// One vote per (option, participant) — DOMAIN_MODEL §6.3. That pair is the row's identity: the
/// Kernel mints no PollVoteId because the vote has a natural composite key. Revising a vote
/// overwrites the choice and never touches the participant's
/// <see cref="PollParticipant.FirstResponseAt"/> (D-172).
/// </summary>
public sealed class PollVote
{
    private PollVote()
    {
    }

    private PollVote(PollId pollId, PollOptionId optionId, ParticipantRef participant, VoteChoice choice, Instant now)
    {
        PollId = pollId;
        OptionId = optionId;
        UserId = participant.UserId;
        GuestIdentityId = participant.GuestIdentityId;
        Choice = choice;
        SubmittedAt = now;
        UpdatedAt = now;
    }

    public PollId PollId { get; private set; }

    public PollOptionId OptionId { get; private set; }

    public UserId? UserId { get; private set; }

    public GuestIdentityId? GuestIdentityId { get; private set; }

    /// <summary>Available | Maybe | Unavailable (D-033).</summary>
    public VoteChoice Choice { get; private set; }

    public Instant SubmittedAt { get; private set; }

    public Instant UpdatedAt { get; private set; }

    public ParticipantRef Participant => ParticipantRef.Create(UserId, GuestIdentityId);

    public bool Matches(PollOptionId optionId, ParticipantRef participant) =>
        OptionId == optionId && UserId == participant.UserId && GuestIdentityId == participant.GuestIdentityId;

    public bool CastBy(ParticipantRef participant) =>
        UserId == participant.UserId && GuestIdentityId == participant.GuestIdentityId;

    internal static PollVote Create(
        PollId pollId,
        PollOptionId optionId,
        ParticipantRef participant,
        VoteChoice choice,
        Instant now) => new(pollId, optionId, participant, choice, now);

    /// <summary>D-225: the vote follows its participant onto the registered account, choice and timestamps intact.</summary>
    internal void ReassignToUser(UserId userId)
    {
        if (GuestIdentityId is null)
        {
            throw new DomainException(DomainError.InvalidTransition, "Only a guest vote is reassigned to a user (D-225).");
        }

        UserId = userId;
        GuestIdentityId = null;
    }

    internal void Revise(VoteChoice choice, Instant now)
    {
        Choice = choice;
        UpdatedAt = now;
    }
}
