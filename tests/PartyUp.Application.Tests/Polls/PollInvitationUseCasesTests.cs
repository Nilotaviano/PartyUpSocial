using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Application.Polls;
using PartyUp.Application.Tests;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Polls;

/// <summary>
/// D-193: <see cref="PollInvitationUseCases.InviteRegisteredUsersAsync"/>, mirroring the P3-LK-01
/// lock-ordering/eligibility coverage <see cref="CreatePollUseCase"/> already proves for creation-time
/// invitees and the fakes <see cref="PollCommandTransactionTests"/> already wires — reused here rather
/// than redefined so the two files' fakes never drift apart.
/// </summary>
public sealed class PollInvitationUseCasesTests
{
    [Fact]
    public async Task Invite_LocksTheOrganizerAndInviteeSetThenThePollParent_D193()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();

        PollCommandResult result = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [fixture.NewFriendId()], default);

        Assert.True(result.Succeeded);
        Assert.Single(fixture.Locks.LockedUserBatches);
        Assert.Equal(2, fixture.Locks.LockedUserBatches[0].Count);
        Assert.Single(fixture.Locks.LockedPollBatches);
        Assert.Equal(pollId, Assert.Single(fixture.Locks.LockedPollBatches[0]));
    }

    [Fact]
    public async Task Invite_RejectsANonFriendAsUserNotFound_D193_D020()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();
        UserId stranger = UserId.New();

        PollCommandResult result = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [stranger], default);

        Assert.False(result.Succeeded);
        Assert.Equal("user_not_found", result.Code);
        Assert.Null(fixture.Polls.All.Single().FindParticipant(ParticipantRef.ForUser(stranger)));
    }

    [Fact]
    public async Task Invite_RejectsABlockedFriendAsUserNotFound_D193_D065()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();
        UserId friendAndBlocked = fixture.NewFriendId();
        fixture.Blocks.Add(Block.Create(
            fixture.OrganizerId, friendAndBlocked, Username.Create("blocked1"), fixture.Clock));

        PollCommandResult result = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [friendAndBlocked], default);

        Assert.False(result.Succeeded);
        Assert.Equal("user_not_found", result.Code);
    }

    /// <summary>
    /// Non-organizer callers get the same fail-closed <c>poll_not_found</c> a missing poll gives —
    /// never a state-revealing error — mirroring <c>SessionInvitationUseCases</c>' probe step.
    /// </summary>
    [Fact]
    public async Task Invite_RejectsANonOrganizerActorAsPollNotFound_D193()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();
        UserId notTheOrganizer = fixture.NewFriendId();

        PollCommandResult result = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, notTheOrganizer, [UserId.New()], default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_not_found", result.Code);
    }

    [Fact]
    public async Task Invite_RejectsAnUnknownPollAsPollNotFound_D193()
    {
        Fixture fixture = new();

        PollCommandResult result = await fixture.UseCase()
            .InviteRegisteredUsersAsync(PollId.New(), fixture.OrganizerId, [UserId.New()], default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_not_found", result.Code);
    }

    [Fact]
    public async Task Invite_RejectsAnEmptyOrOversizedInviteeList_D193()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();

        PollCommandResult empty = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [], default);
        Assert.False(empty.Succeeded);
        Assert.Equal("poll_validation", empty.Code);

        List<UserId> tooMany = [.. Enumerable.Range(0, PollInvitationUseCases.MaxInviteeCount + 1).Select(_ => UserId.New())];
        PollCommandResult oversized = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, tooMany, default);
        Assert.False(oversized.Succeeded);
        Assert.Equal("poll_validation", oversized.Code);
    }

    [Fact]
    public async Task Invite_MaterializesTheInAppNotificationInTransaction_D193_D112_A016()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();
        UserId invitee = fixture.NewFriendId();

        await fixture.UseCase().InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [invitee], default);

        Assert.Equal(1, fixture.Materializer.Calls);
        PollInvitationSent raised = Assert.Single(fixture.Materializer.Events.OfType<PollInvitationSent>());
        Assert.Equal(invitee, raised.InviteeId);
        Assert.Equal(pollId, raised.PollId);
    }

    [Fact]
    public async Task Invite_DispatchesTheD189DirectPollInviteEmail_D193_D189()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();
        UserId invitee = fixture.NewFriendId();

        FakeEmailRecipientReader reader = new();
        reader.Recipients[fixture.OrganizerId] = new EmailRecipient(
            fixture.OrganizerId, "organizer@example.test", "Organizer", Locale.Create("en"), null, true);
        reader.Recipients[invitee] = new EmailRecipient(
            invitee, "invitee@example.test", "Invitee", Locale.Create("en"), null, true);
        FakeEmailDispatcher dispatcher = new();
        InviteEmailService inviteEmails = TestInviteEmailService.Create(reader: reader, dispatcher: dispatcher);

        await fixture.UseCase(inviteEmails)
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [invitee], default);

        EmailMessage message = Assert.Single(dispatcher.Messages);
        Assert.Equal("invitee@example.test", message.ToAddress);
        Assert.Equal(EmailTemplate.PollInvitation, message.Template);
    }

    [Fact]
    public async Task Invite_AddsAnActiveParticipantWithNoVotes_D193_D033()
    {
        Fixture fixture = new();
        PollId pollId = await fixture.SeedPollAsync();
        UserId invitee = fixture.NewFriendId();

        PollCommandResult result = await fixture.UseCase()
            .InviteRegisteredUsersAsync(pollId, fixture.OrganizerId, [invitee], default);

        Assert.True(result.Succeeded);
        PollParticipant row = fixture.Polls.All.Single().FindParticipant(ParticipantRef.ForUser(invitee))!;
        Assert.True(row.IsActive);
        Assert.Null(row.FirstResponseAt);
    }

    private sealed class Fixture
    {
        internal Fixture()
        {
            Clock = new MutableClock(Instant.FromUtc(2026, 3, 2, 12, 0));
            Polls = new FakePollRepository();
            Locks = new FakePollLockRepository();
            Friendships = new FakeFriendshipRepository();
            Blocks = new FakeBlockRepository();
            Materializer = new RecordingPollNotificationMaterializer();
            UnitOfWork = new RollbackAwarePollUnitOfWork(Polls);

            (OrganizerId, _firstFriendId) = Friendships.MakeAccepted(Clock);
        }

        internal MutableClock Clock { get; }

        internal FakePollRepository Polls { get; }

        internal FakePollLockRepository Locks { get; }

        internal FakeFriendshipRepository Friendships { get; }

        internal FakeBlockRepository Blocks { get; }

        internal RecordingPollNotificationMaterializer Materializer { get; }

        internal RollbackAwarePollUnitOfWork UnitOfWork { get; }

        internal UserId OrganizerId { get; }

        private readonly UserId _firstFriendId;
        private bool _firstFriendConsumed;

        /// <summary>Returns an id already Accepted-friended with the organizer, minting a fresh one after the first call.</summary>
        internal UserId NewFriendId()
        {
            if (!_firstFriendConsumed)
            {
                _firstFriendConsumed = true;
                return _firstFriendId;
            }

            return Friendships.MakeAcceptedWith(OrganizerId, Clock);
        }

        /// <summary>
        /// Seeds a private, guest-eligible poll (D-125 lets a private poll skip the ≥1-invitee floor)
        /// through <see cref="CreatePollUseCase"/> and a throwaway materializer/unit-of-work, so the
        /// fixture's own <see cref="Materializer"/> only ever records the invite command under test.
        /// </summary>
        internal async Task<PollId> SeedPollAsync()
        {
            var seedUseCase = new CreatePollUseCase(
                Polls,
                new FakePollLockRepository(),
                Friendships,
                Blocks,
                new FakeFriendGroupRepository(),
                new RecordingPollNotificationMaterializer(),
                new RollbackAwarePollUnitOfWork(Polls),
                Clock,
                TestInviteEmailService.Create());

            CreatePollCommand command = new(
                OrganizerId,
                "Raid poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Private,
                AudienceGroupIds: null,
                OptionStartTimes: [Clock.GetCurrentInstant() + Duration.FromHours(5), Clock.GetCurrentInstant() + Duration.FromHours(6)],
                DeadlineChoice: null,
                InviteeUserIds: [],
                GuestLinkCount: null);

            CreatePollResult result = await seedUseCase.ExecuteAsync(command, default);
            return result.Poll!.Id;
        }

        internal PollInvitationUseCases UseCase(InviteEmailService? inviteEmails = null) =>
            new(
                Polls,
                Locks,
                Friendships,
                Blocks,
                Materializer,
                UnitOfWork,
                Clock,
                inviteEmails ?? TestInviteEmailService.Create());
    }
}
