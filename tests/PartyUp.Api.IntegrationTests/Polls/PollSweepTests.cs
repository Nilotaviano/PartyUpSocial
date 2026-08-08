using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// S3b's <see cref="PollDueSweepJob"/> gate (PHASE6_DESIGN.md §3): the bounded due-sweep predicate
/// finds every poll whose deadline or selection window has passed, applies the same shared
/// <see cref="PollTransitionUseCase"/> (i.e. <see cref="Poll.ApplyDueTransitions"/>) every request
/// path uses, and does so idempotently -- one job covering both the Close (D-079) and Expire
/// (D-088/D-170) transitions.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollSweepTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RepeatedSweep_ClosesAtDeadline_IsIdempotent_D079()
    {
        PollId pollId = await SeedPollAsync();

        // Deadline is Hours6 from creation; advance just past it.
        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await RunSweepAsync();
        Assert.Equal(PollStatus.Closed, (await ReloadAsync(pollId)).Status);

        // A second sweep over the same, already-Closed poll must be a pure no-op: the status column
        // is what the second pass reads and trusts (A-067), not re-derived from the deadline again.
        await RunSweepAsync();
        Assert.Equal(PollStatus.Closed, (await ReloadAsync(pollId)).Status);
    }

    [Fact]
    public async Task RepeatedSweep_ExpiresAtSelectionWindowEnd_IsIdempotentAndNotifies_D088_D170()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId) = await SeedPollWithParticipantsAsync();

        // Deadline is Hours6; the selection window ends 48h after that. Advancing past both in one
        // jump proves a poll left unswept past both boundaries closes then expires in one pass.
        _factory.Clock.Advance(Duration.FromHours(6 + 48) + Duration.FromMinutes(1));

        await RunSweepAsync();
        Assert.Equal(PollStatus.Expired, (await ReloadAsync(pollId)).Status);

        await using (AsyncServiceScope readScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (UserId recipient in new[] { organizerId, inviteeId })
            {
                Assert.True(await context.Notifications.AsNoTracking().AnyAsync(n =>
                    n.PollId == pollId && n.RecipientUserId == recipient && n.Type == NotificationType.PollExpired));
            }
        }

        // A second sweep must not duplicate the expiry notification.
        await RunSweepAsync();
        await using AsyncServiceScope secondReadScope = _factory.Services.CreateAsyncScope();
        AppDbContext secondReadContext = secondReadScope.ServiceProvider.GetRequiredService<AppDbContext>();
        int expiredNotificationCount = await secondReadContext.Notifications.AsNoTracking()
            .CountAsync(n => n.PollId == pollId && n.Type == NotificationType.PollExpired);
        Assert.Equal(2, expiredNotificationCount);
    }

    [Fact]
    public async Task CandidatePredicateTrustsTheMaterializedStatusColumn()
    {
        // A poll already Canceled before its deadline passes must never be picked up by the sweep --
        // the candidate predicate is status IN (Polling, Closed), never a timestamp re-derivation.
        PollId pollId = await SeedPollAsync();

        await using (AsyncServiceScope cancelScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult canceled = await PollTestHelpers.BuildCancelUseCase(cancelScope)
                .CancelAsync(pollId, await OrganizerIdFor(pollId), default);
            Assert.True(canceled.Succeeded, canceled.Code);
        }

        _factory.Clock.Advance(Duration.FromHours(60));

        await RunSweepAsync();
        Assert.Equal(PollStatus.Canceled, (await ReloadAsync(pollId)).Status);
    }

    private async Task<UserId> OrganizerIdFor(PollId pollId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Poll poll = await context.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        return poll.OrganizerId;
    }

    private async Task RunSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<PollDueSweepJob>();
        await job.ExecuteAsync(default);
    }

    private async Task<Poll> ReloadAsync(PollId id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Polls.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task<PollId> SeedPollAsync()
    {
        (PollId pollId, _, _) = await SeedPollWithParticipantsAsync();
        return pollId;
    }

    private async Task<(PollId PollId, UserId OrganizerId, UserId InviteeId)> SeedPollWithParticipantsAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-sweep-organizer-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"poll-sweep-invitee-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Sweep test poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: false,
                OptionStartTimes: [now + Duration.FromHours(2), now + Duration.FromHours(3)],
                DeadlineChoice: PollDeadlineChoice.Hours6,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId, inviteeId);
    }
}
