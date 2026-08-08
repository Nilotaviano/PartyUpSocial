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
/// S3b's D-120 "deadline approaching" gate (PHASE6_DESIGN.md §3, DOMAIN_MODEL:227): a scheduled
/// emission fired once per recipient at Deadline - 1h, durably deduplicated by
/// <c>ux_notifications_poll_deadline_once</c> rather than by application-level bookkeeping, and
/// skipped once the poll has left <c>Polling</c> by the time the job reaches it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollDeadlineApproachingJobTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task DoubleRun_EmitsOncePerRecipient_D120()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId) = await SeedPollAsync();

        // Deadline is Hours6 from creation; land inside the [Deadline-1h, Deadline) notice window.
        _factory.Clock.Advance(Duration.FromHours(5) + Duration.FromMinutes(1));

        await RunJobAsync();
        await AssertExactlyOneNoticeEach(pollId, organizerId, inviteeId);

        // A second run over the same still-Polling, still-in-window poll must not duplicate the
        // notice -- the partial unique index is what proves this, not a marker this test can see.
        await RunJobAsync();
        await AssertExactlyOneNoticeEach(pollId, organizerId, inviteeId);
    }

    [Fact]
    public async Task SkippedOncePollLeavesPolling_D120()
    {
        (PollId pollId, UserId organizerId, _) = await SeedPollAsync();

        _factory.Clock.Advance(Duration.FromHours(5) + Duration.FromMinutes(1));

        await using (AsyncServiceScope cancelScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult canceled = await PollTestHelpers.BuildCancelUseCase(cancelScope)
                .CancelAsync(pollId, organizerId, default);
            Assert.True(canceled.Succeeded, canceled.Code);
        }

        await RunJobAsync();

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.Notifications.AsNoTracking().AnyAsync(n =>
            n.PollId == pollId && n.Type == NotificationType.PollDeadlineApproaching));
    }

    [Fact]
    public async Task NotSentBeforeTheNoticeWindowOpens_D120()
    {
        (PollId pollId, _, _) = await SeedPollAsync();

        // Well before Deadline-1h.
        _factory.Clock.Advance(Duration.FromMinutes(30));

        await RunJobAsync();

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.Notifications.AsNoTracking().AnyAsync(n =>
            n.PollId == pollId && n.Type == NotificationType.PollDeadlineApproaching));
    }

    private async Task AssertExactlyOneNoticeEach(PollId pollId, UserId organizerId, UserId inviteeId)
    {
        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (UserId recipient in new[] { organizerId, inviteeId })
        {
            int count = await context.Notifications.AsNoTracking().CountAsync(n =>
                n.PollId == pollId
                && n.RecipientUserId == recipient
                && n.Type == NotificationType.PollDeadlineApproaching);
            Assert.Equal(1, count);
        }
    }

    private async Task RunJobAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<PollDeadlineApproachingJob>();
        await job.ExecuteAsync(default);
    }

    private async Task<(PollId PollId, UserId OrganizerId, UserId InviteeId)> SeedPollAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-deadline-organizer-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"poll-deadline-invitee-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Deadline notice test poll",
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
