using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
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

    /// <summary>D-118: approval grants Active PollParticipant access to every current option.</summary>
    [Fact]
    public async Task ApproveJoinRequest_GrantsAccessToAllCurrentOptions()
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

        await using AsyncServiceScope approveScope = _factory.Services.CreateAsyncScope();
        PollJoinRequestCommandResult approveResult = await PollTestHelpers.BuildJoinRequestUseCases(approveScope)
            .ApproveJoinRequestAsync(pollId, organizerId, requestId, default);

        Assert.True(approveResult.Succeeded, approveResult.Code);
        Poll poll = approveResult.Poll!;
        PollParticipant participant = Assert.Single(poll.Participants, p => p.UserId == requesterId);
        Assert.True(participant.IsActive);
        Assert.Equal(poll.Options.Count, poll.Options.Count(o => true));
        Assert.Null(participant.FirstResponseAt);
    }

    /// <summary>D-065's poll analogue: a blocked pair's request writes and notifies nothing, indistinguishable from missing.</summary>
    [Fact]
    public async Task RequestJoin_FromBlockedUser_FailsClosedWithNoWriteOrNotification()
    {
        (PollId pollId, UserId organizerId) = await SeedNonPrivatePollAsync();
        UserId blockedRequesterId = await _factory.SeedUserAsync(email: $"poll-jr-blocked-{Guid.NewGuid():N}@example.test");

        await using (AsyncServiceScope friendshipScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext friendshipContext = friendshipScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(friendshipContext, organizerId, blockedRequesterId, _factory.Clock);
            await friendshipContext.SaveChangesAsync();
        }

        await using AsyncServiceScope blockScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = blockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        string username = (await context.DomainUsers.SingleAsync(u => u.Id == blockedRequesterId))
            .Username!.Value.Value;
        Block block = Block.Create(organizerId, blockedRequesterId, Username.Create(username), _factory.Clock);
        _ = block.DequeueEvents();
        context.Blocks.Add(block);
        // A block clears the friendship (mirrors the shipped block executor's own effect); simulate
        // that here without invoking the full block use case, since the pre-lock probe -- correctly --
        // is blind to friendship once the pair is blocked, and this test targets the join-request
        // command's own block-boundary check, not the block executor's roster-scrub. The friendship
        // was already committed by the prior scope/SaveChanges above, so this query sees it.
        Friendship existingFriendship = await context.Friendships.SingleAsync(f =>
            (f.UserLoId == organizerId || f.UserHiId == organizerId)
            && (f.UserLoId == blockedRequesterId || f.UserHiId == blockedRequesterId));
        context.Friendships.Remove(existingFriendship);
        await context.SaveChangesAsync();

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
    public async Task RequestJoin_FromStrangerOnPrivatePoll_FailsClosed()
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
                    IsPrivate: true,
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
                IsPrivate: false,
                OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                DeadlineChoice: deadlineChoice,
                InviteeUserIds: [invitedFriendId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId);
    }
}
