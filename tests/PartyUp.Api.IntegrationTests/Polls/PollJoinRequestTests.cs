using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>Join requests (PHASE6_DESIGN.md §2 §5.3): the D-118 open window, approval granting all current options, and the block/stranger fail-closed 404 that writes and notifies nothing.</summary>
[Trait("Category", "Integration")]
public sealed class PollJoinRequestTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RequestJoin_WhilePolling_Succeeds_D118()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync();
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-jr-requester-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult result = await PollTestHelpers.BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(pollId, requesterId, default);

        Assert.True(result.Succeeded, result.Code);
        Assert.NotNull(result.JoinRequest);
    }

    /// <summary>
    /// D-118: the window is Polling ∧ now &lt; Deadline -- once the deadline has passed, visibility
    /// still resolves (the requester is a friend of the organizer), so the aggregate's own
    /// <c>RequireVotingOpen</c> guard is what rejects it, surfacing as the voting-closed code rather
    /// than a visibility 404.
    /// </summary>
    [Fact]
    public async Task RequestJoin_AfterDeadline_Rejected_D118()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync(PollDeadlineChoice.Hours6);
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-jr-late-requester-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        _factory.Clock.Advance(Duration.FromHours(7));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult result = await PollTestHelpers.BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(pollId, requesterId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_voting_closed", result.Code);
    }

    /// <summary>
    /// D-118: approval grants the new Active PollParticipant access to every current option -- proven
    /// by actually voting on all of them, including one the organizer suggested AFTER the request was
    /// opened (so a stale option snapshot captured at request time could never satisfy this). If
    /// approval instead attached the participant without option access, or option access were resolved
    /// at request-open time rather than freshly, the vote on the late-suggested option would fail here.
    /// </summary>
    [Fact]
    public async Task ApproveJoinRequest_GrantsAccessToAllCurrentOptions_D118()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync();
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-jr-approve-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        JoinRequestId requestId;
        await using (AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope())
        {
            PollJoinRequestCommandResult requestResult = await PollTestHelpers.BuildJoinRequestUseCases(requestScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(requestResult.Succeeded, requestResult.Code);
            requestId = requestResult.JoinRequest!.Id;
        }

        // A new option lands AFTER the request was opened -- the approval-time access grant must still
        // cover it, not just the options that existed when the requester asked to join.
        PollOptionId lateOptionId;
        await using (AsyncServiceScope suggestScope = _factory.Services.CreateAsyncScope())
        {
            Instant now = _factory.Clock.GetCurrentInstant();
            SuggestOptionResult suggestResult = await PollTestHelpers.BuildOptionUseCases(suggestScope)
                .SuggestOptionAsync(pollId, organizerId, now + Duration.FromHours(4), null, default);
            Assert.True(suggestResult.Succeeded, suggestResult.Code);
            lateOptionId = suggestResult.OptionId!.Value;
        }

        await using AsyncServiceScope approveScope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult approveResult = await PollTestHelpers.BuildJoinRequestUseCases(approveScope)
            .ApproveJoinRequestAsync(pollId, organizerId, requestId, default);

        Assert.True(approveResult.Succeeded, approveResult.Code);
        Poll poll = approveResult.Poll!;
        PollParticipant participant = Assert.Single(poll.Participants, p => p.UserId == requesterId);
        Assert.True(participant.IsActive);
        Assert.Null(participant.FirstResponseAt);
        Assert.Contains(poll.Options, o => o.Id == lateOptionId);

        foreach (PollOption option in poll.Options)
        {
            await using AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope();
            PollCommandResult voteResult = await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, requesterId, option.Id, VoteChoice.Available, default);
            Assert.True(voteResult.Succeeded, voteResult.Code);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        int voteRowCount = await verifyContext.Set<PollVote>()
            .CountAsync(v => v.PollId == pollId && v.UserId == requesterId);
        Assert.Equal(poll.Options.Count, voteRowCount);
        Assert.True(voteRowCount >= 3, "Expected the two original options plus the late-suggested one.");
    }

    /// <summary>
    /// D-065's poll analogue, discriminating on the block itself rather than on friendlessness: the
    /// requester is BLOCKED with the organizer but stays FRIENDS with a clean extender -- an invited,
    /// Active registered participant who never voted (so §7 rule 2 would admit the requester through
    /// inherited access if the block rule vanished). A build that forgot the block check, or that
    /// re-evaluated visibility from stale pre-block friendship facts, would let this succeed; the
    /// block-boundary check must fail it closed regardless of the still-intact extender path.
    /// </summary>
    [Fact]
    public async Task RequestJoin_FromBlockedUser_FailsClosedWithNoWriteOrNotification_D118()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-jr-blocked-organizer-{Guid.NewGuid():N}@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: $"poll-jr-blocked-extender-{Guid.NewGuid():N}@example.test");
        UserId blockedRequesterId = await _factory.SeedUserAsync(email: $"poll-jr-blocked-{Guid.NewGuid():N}@example.test");

        PollId pollId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Organizer <-> extender: required for the extender to be a legitimate invitee (D-125).
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, extenderId, _factory.Clock);
            // Extender <-> requester: the inherited-access path the block must still defeat.
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, extenderId, blockedRequesterId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            Instant startTime = now + Duration.FromHours(2);
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Blocked requester inherited-access test",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [extenderId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
            // The extender is an Active registered participant with no votes yet -- PollVisibility's
            // §7 rule 2 (ExtendsAccess) already admits that shape, so no vote is needed to prove the
            // inherited path is genuinely open before the block shuts it.
            Assert.Contains(created.Poll!.Participants, p => p.UserId == extenderId && p.IsActive);
        }

        await using AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope();
        AppDbContext blockContext = blockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        string username = (await blockContext.DomainUsers.SingleAsync(u => u.Id == blockedRequesterId))
            .Username!.Value.Value;
        Block block = Block.Create(organizerId, blockedRequesterId, Username.Create(username), _factory.Clock);
        _ = block.DequeueEvents();
        blockContext.Blocks.Add(block);
        await blockContext.SaveChangesAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult result = await PollTestHelpers.BuildJoinRequestUseCases(scope)
            .RequestJoinAsync(pollId, blockedRequesterId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_not_found", result.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyContext.JoinRequests.CountAsync(r => r.PollId == pollId && r.RequesterId == blockedRequesterId));
        Assert.Equal(0, await verifyContext.Notifications.CountAsync(n => n.PollId == pollId && n.RecipientUserId == organizerId));
    }

    /// <summary>A private poll's uninvited stranger cannot even resolve visibility -- same indistinguishable 404, no write.</summary>
    [Fact]
    public async Task RequestJoin_FromStrangerOnPrivatePoll_FailsClosed_D118()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-jr-private-organizer-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"poll-jr-private-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"poll-jr-stranger-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, invitedFriendId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            Instant startTime = now + Duration.FromHours(2);
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Private stranger test",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Private,
            AudienceGroupIds: null,
                    OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [invitedFriendId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);

            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            PollJoinRequestCommandResult result = await PollTestHelpers.BuildJoinRequestUseCases(scope)
                .RequestJoinAsync(created.Poll!.Id, strangerId, default);

            Assert.False(result.Succeeded);
            Assert.Equal("poll_not_found", result.Code);
        }
    }

    /// <summary>
    /// D-107: withdrawing a pending request frees the (poll, requester) partial unique index it held --
    /// proven by a fresh RequestJoin succeeding afterward, which a still-open row (or a lingering
    /// unique-violation) would reject.
    /// </summary>
    [Fact]
    public async Task WithdrawJoinRequest_FreesThePartialUniqueForANewPendingRequest_D107()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync();
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-jr-withdraw-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        JoinRequestId firstRequestId;
        await using (AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope())
        {
            PollJoinRequestCommandResult firstRequest = await PollTestHelpers.BuildJoinRequestUseCases(requestScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(firstRequest.Succeeded, firstRequest.Code);
            firstRequestId = firstRequest.JoinRequest!.Id;
        }

        await using (AsyncServiceScope withdrawScope = _factory.Services.CreateAsyncScope())
        {
            PollJoinRequestCommandResult withdrawResult = await PollTestHelpers.BuildJoinRequestUseCases(withdrawScope)
                .WithdrawJoinRequestAsync(pollId, requesterId, default);
            Assert.True(withdrawResult.Succeeded, withdrawResult.Code);
        }

        await using (AsyncServiceScope secondRequestScope = _factory.Services.CreateAsyncScope())
        {
            PollJoinRequestCommandResult secondRequest = await PollTestHelpers.BuildJoinRequestUseCases(secondRequestScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(secondRequest.Succeeded, secondRequest.Code);
            Assert.NotEqual(firstRequestId, secondRequest.JoinRequest!.Id);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verifyContext.JoinRequests
            .CountAsync(r => r.PollId == pollId && r.RequesterId == requesterId && r.Status == JoinRequestStatus.Pending));
    }

    /// <summary>D-107: rejection notifies only the requester, who holds no participant row either way.</summary>
    [Fact]
    public async Task RejectJoinRequest_NotifiesOnlyTheRequester_D107()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync();
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-jr-reject-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        JoinRequestId requestId;
        await using (AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope())
        {
            PollJoinRequestCommandResult requestResult = await PollTestHelpers.BuildJoinRequestUseCases(requestScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(requestResult.Succeeded, requestResult.Code);
            requestId = requestResult.JoinRequest!.Id;
        }

        await using AsyncServiceScope rejectScope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult rejectResult = await PollTestHelpers.BuildJoinRequestUseCases(rejectScope)
            .RejectJoinRequestAsync(pollId, organizerId, requestId, default);
        Assert.True(rejectResult.Succeeded, rejectResult.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyContext.Set<PollParticipant>().CountAsync(p => p.PollId == pollId && p.UserId == requesterId));
        Notification notification = await verifyContext.Notifications
            .SingleAsync(n => n.PollId == pollId && n.Type == NotificationType.PollJoinRequestRejected);
        Assert.Equal(requesterId, notification.RecipientUserId);
    }

    /// <summary>
    /// D-118: once the poll has left Polling (deadline reached), any still-pending request is Lapsed by
    /// the same due-transition sweep RejectJoinRequest applies before re-checking pending status -- the
    /// design's "no after-close reject semantics" surfaces as an invalid-transition error rather than a
    /// silently-accepted reject on a request that is no longer actionable.
    /// </summary>
    [Fact]
    public async Task RejectJoinRequest_AfterClose_SurfacesInvalidTransition_D118()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync(PollDeadlineChoice.Hours6);
        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-jr-reject-close-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = friendScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        JoinRequestId requestId;
        await using (AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope())
        {
            PollJoinRequestCommandResult requestResult = await PollTestHelpers.BuildJoinRequestUseCases(requestScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(requestResult.Succeeded, requestResult.Code);
            requestId = requestResult.JoinRequest!.Id;
        }

        _factory.Clock.Advance(Duration.FromHours(7));

        await using AsyncServiceScope rejectScope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult rejectResult = await PollTestHelpers.BuildJoinRequestUseCases(rejectScope)
            .RejectJoinRequestAsync(pollId, organizerId, requestId, default);

        Assert.False(rejectResult.Succeeded);
        Assert.Equal("poll_invalid_transition", rejectResult.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        JoinRequest storedRequest = await verifyContext.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(JoinRequestStatus.Lapsed, storedRequest.Status);
    }

    private async Task<(PollId PollId, UserId OrganizerId)> SeedNonPrivatePollAsync(
        PollDeadlineChoice deadlineChoice = PollDeadlineChoice.Hours24)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-jr-organizer-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"poll-jr-invited-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, invitedFriendId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        Instant startTime = now + Duration.FromHours(2);
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Join request test poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                DeadlineChoice: deadlineChoice,
                InviteeUserIds: [invitedFriendId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId);
    }
}
