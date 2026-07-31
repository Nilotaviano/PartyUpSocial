using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.Kernel;

public class EnumTests
{
    [Fact]
    public void ReminderOffset_OffMeansNoReminder_D082()
    {
        Assert.Null(ReminderOffset.Off.ToLeadTime());
        Assert.Null(ReminderOffset.Off.ReminderInstantFor(Instant.FromUtc(2026, 7, 30, 20, 0)));
    }

    [Theory]
    [InlineData(ReminderOffset.Min15, 15)]
    [InlineData(ReminderOffset.Min30, 30)]
    [InlineData(ReminderOffset.Hours1, 60)]
    [InlineData(ReminderOffset.Hours3, 180)]
    [InlineData(ReminderOffset.Hours12, 720)]
    [InlineData(ReminderOffset.Hours24, 1440)]
    public void ReminderOffset_MapsToItsLeadTime_D082(ReminderOffset offset, int expectedMinutes)
    {
        Assert.Equal(Duration.FromMinutes(expectedMinutes), offset.ToLeadTime());
    }

    [Fact]
    public void ReminderOffset_OffersExactlyTheSevenSpecChoices_D082()
    {
        Assert.Equal(
            [
                ReminderOffset.Off,
                ReminderOffset.Min15,
                ReminderOffset.Min30,
                ReminderOffset.Hours1,
                ReminderOffset.Hours3,
                ReminderOffset.Hours12,
                ReminderOffset.Hours24,
            ],
            Enum.GetValues<ReminderOffset>());
    }

    [Fact]
    public void ReminderOffset_DefaultsToOneHour_D082()
    {
        Assert.Equal(ReminderOffset.Hours1, ReminderOffsetExtensions.Default);
        Assert.Equal(Duration.FromHours(1), ReminderOffsetExtensions.Default.ToLeadTime());
    }

    [Fact]
    public void ReminderInstantFor_SubtractsTheLeadTimeFromStart_D082()
    {
        Instant start = Instant.FromUtc(2026, 7, 30, 20, 0);

        Assert.Equal(Instant.FromUtc(2026, 7, 30, 19, 0), ReminderOffset.Hours1.ReminderInstantFor(start));
        Assert.Equal(Instant.FromUtc(2026, 7, 29, 20, 0), ReminderOffset.Hours24.ReminderInstantFor(start));
    }

    [Fact]
    public void ReminderOffset_RejectsAnUndefinedValue()
    {
        DomainException error = Assert.Throws<DomainException>(() => ((ReminderOffset)99).ToLeadTime());

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Theory]
    [InlineData(PollDeadlineChoice.Hours6, 6)]
    [InlineData(PollDeadlineChoice.Hours12, 12)]
    [InlineData(PollDeadlineChoice.Hours24, 24)]
    [InlineData(PollDeadlineChoice.Hours48, 48)]
    public void PollDeadlineChoice_MapsToItsDuration_D070(PollDeadlineChoice choice, int expectedHours)
    {
        Assert.Equal(Duration.FromHours(expectedHours), choice.ToDuration());
    }

    [Fact]
    public void PollDeadlineChoice_OffersExactlyTheFourSpecChoices_D070()
    {
        Assert.Equal(
            [
                PollDeadlineChoice.Hours6,
                PollDeadlineChoice.Hours12,
                PollDeadlineChoice.Hours24,
                PollDeadlineChoice.Hours48,
            ],
            Enum.GetValues<PollDeadlineChoice>());
    }

    [Fact]
    public void DeadlineFrom_AddsTheChoiceToCreationTime_D070()
    {
        Instant createdAt = Instant.FromUtc(2026, 7, 30, 20, 0);

        Assert.Equal(
            Instant.FromUtc(2026, 7, 31, 20, 0),
            PollDeadlineChoice.Hours24.DeadlineFrom(createdAt));
    }

    [Fact]
    public void PollDeadlineChoice_RejectsAnUndefinedValue()
    {
        DomainException error = Assert.Throws<DomainException>(() => ((PollDeadlineChoice)99).ToDuration());

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void SessionStatus_HasTheFiveSpecStatesWithStablePersistedValues()
    {
        Assert.Equal(0, (int)SessionStatus.Unconfirmed);
        Assert.Equal(1, (int)SessionStatus.Confirmed);
        Assert.Equal(2, (int)SessionStatus.Completed);
        Assert.Equal(3, (int)SessionStatus.Expired);
        Assert.Equal(4, (int)SessionStatus.Canceled);
        Assert.Equal(5, Enum.GetValues<SessionStatus>().Length);
    }

    [Fact]
    public void ParticipationStatus_HasTheFiveSpecStatesWithStablePersistedValues()
    {
        Assert.Equal(0, (int)ParticipationStatus.Pending);
        Assert.Equal(1, (int)ParticipationStatus.Going);
        Assert.Equal(2, (int)ParticipationStatus.Maybe);
        Assert.Equal(3, (int)ParticipationStatus.Declined);
        Assert.Equal(4, (int)ParticipationStatus.Removed);
        Assert.Equal(5, Enum.GetValues<ParticipationStatus>().Length);
    }

    [Fact]
    public void ParticipationOrigin_CoversEveryRosterEntryPath()
    {
        Assert.Equal(
            [
                ParticipationOrigin.Invited,
                ParticipationOrigin.JoinRequest,
                ParticipationOrigin.GuestLink,
                ParticipationOrigin.PollFinalization,
            ],
            Enum.GetValues<ParticipationOrigin>());
    }

    [Fact]
    public void JoinRequestStatus_CoversTheOpenAndClosedOutcomes()
    {
        Assert.Equal(
            [
                JoinRequestStatus.Pending,
                JoinRequestStatus.Approved,
                JoinRequestStatus.Rejected,
                JoinRequestStatus.Withdrawn,
                JoinRequestStatus.Lapsed,
            ],
            Enum.GetValues<JoinRequestStatus>());
    }

    [Fact]
    public void GuestLinkState_CoversEveryRevocationCause()
    {
        Assert.Equal(
            [
                GuestLinkState.Active,
                GuestLinkState.RevokedByCancel,
                GuestLinkState.RevokedByRemoval,
                GuestLinkState.ExpiredPoll,
            ],
            Enum.GetValues<GuestLinkState>());
    }

    [Fact]
    public void PollStatus_HasTheFiveSpecStatesWithStablePersistedValues()
    {
        Assert.Equal(0, (int)PollStatus.Polling);
        Assert.Equal(1, (int)PollStatus.Closed);
        Assert.Equal(2, (int)PollStatus.Finalized);
        Assert.Equal(3, (int)PollStatus.Expired);
        Assert.Equal(4, (int)PollStatus.Canceled);
        Assert.Equal(5, Enum.GetValues<PollStatus>().Length);
    }

    [Fact]
    public void VoteChoice_HasTheThreeSpecChoices_D033()
    {
        Assert.Equal(
            [VoteChoice.Available, VoteChoice.Maybe, VoteChoice.Unavailable],
            Enum.GetValues<VoteChoice>());
    }

    [Fact]
    public void Platform_HasTheLaunchList_D040()
    {
        Assert.Equal(
            [Platform.Steam, Platform.PlayStation, Platform.Xbox, Platform.NotApplicable],
            Enum.GetValues<Platform>());
    }

    [Fact]
    public void UserLifecycle_RunsFromOnboardingToDeleted()
    {
        Assert.Equal(
            [UserLifecycle.Onboarding, UserLifecycle.Active, UserLifecycle.PendingDeletion, UserLifecycle.Deleted],
            Enum.GetValues<UserLifecycle>());
    }

    [Fact]
    public void PollParticipantStatus_IsActiveOrRemoved()
    {
        Assert.Equal(
            [PollParticipantStatus.Active, PollParticipantStatus.Removed],
            Enum.GetValues<PollParticipantStatus>());
    }
}
