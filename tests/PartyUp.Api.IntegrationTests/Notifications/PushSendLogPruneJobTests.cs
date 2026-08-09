using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §3/§9 ruling 8's coverage gap: send-log rows older than the 30-day retention are
/// deleted, rows inside it are left alone -- the shipped job (<see cref="PushSendLogPruneJob"/>) had no
/// prior test at all.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PushSendLogPruneJobTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task DeletesRowsOlderThanThirtyDays_KeepsYoungerRows()
    {
        UserId recipientId = await _factory.SeedUserAsync(email: "prune@example.test");
        SessionId sessionId = await SeedSessionAsync(recipientId);
        Guid notificationId = await SeedNotificationAsync(recipientId, sessionId);
        Guid oldSubscriptionId = await SeedSubscriptionAsync(recipientId, "prune-old-sub");
        Guid youngSubscriptionId = await SeedSubscriptionAsync(recipientId, "prune-young-sub");

        Instant now = _factory.Clock.GetCurrentInstant();
        Instant oldClaimedAt = now - Duration.FromDays(31);
        Instant youngClaimedAt = now - Duration.FromDays(1);

        Guid oldSendId = await InsertSendLogRowAsync(notificationId, oldSubscriptionId, oldClaimedAt);
        Guid youngSendId = await InsertSendLogRowAsync(notificationId, youngSubscriptionId, youngClaimedAt);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = new PushSendLogPruneJob(context, _factory.Clock);
            await job.ExecuteAsync(default);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await verifyContext.NotificationPushSends.AsNoTracking().AnyAsync(s => s.Id == oldSendId));
        Assert.True(await verifyContext.NotificationPushSends.AsNoTracking().AnyAsync(s => s.Id == youngSendId));
    }

    private async Task<Guid> InsertSendLogRowAsync(Guid notificationId, Guid subscriptionId, Instant claimedAt)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Guid id = Guid.CreateVersion7();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_push_sends (id, notification_id, push_subscription_id, claimed_at, outcome)
            VALUES ({id}, {notificationId}, {subscriptionId}, {claimedAt}, 0)
            """);
        return id;
    }

    private async Task<SessionId> SeedSessionAsync(UserId organizerId)
    {
        SessionDetails details = new(
            SessionTitle.Create("Prune job test session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        _ = session.DequeueEvents();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<Guid> SeedNotificationAsync(UserId recipientId, SessionId sessionId)
    {
        Notification notification = Notification.Create(
            recipientId, NotificationType.SessionConfirmed, NotificationSubject.ForSession(sessionId), _factory.Clock);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();
        return notification.Id.Value;
    }

    private async Task<Guid> SeedSubscriptionAsync(UserId userId, string endpointSuffix)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();
        PushSubscriptionId id = await repository.UpsertActiveAsync(
            userId,
            $"https://push.example/{endpointSuffix}",
            "public-key",
            "auth-secret",
            _factory.Clock,
            default);
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.SaveChangesAsync();
        return id.Value;
    }
}
