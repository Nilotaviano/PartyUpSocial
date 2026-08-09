using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Api.IntegrationTests.Friends;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §1's deletion tail: <c>UserDataScope.PushSubscriptions</c> becomes real, the step's
/// <c>NotificationMute</c> purge lands beside it, and send-log rows follow their subscription's cascade
/// FK -- all three ahead of the shipped <c>notifications</c> purge, in the same grace-expiry transaction
/// (DOMAIN_MODEL.md §11 step 5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PushTransportDeletionPurgeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ExpiryPurgesSubscriptionsSendLogAndMutesAheadOfTheNotificationsPurge_D092()
    {
        Phase3DeletionSeed seed = await Phase3DeletionTestSupport.SeedAsync(_factory);
        Guid subscriptionId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pushSubscriptions = scope.ServiceProvider.GetRequiredService<IPushSubscriptionRepository>();
            PushSubscriptionId id = await pushSubscriptions.UpsertActiveAsync(
                seed.SubjectId,
                "https://push.example/deletion-purge",
                "public-key",
                "auth-secret",
                _factory.Clock,
                default);
            await context.SaveChangesAsync();
            subscriptionId = id.Value;

            var pushSends = scope.ServiceProvider.GetRequiredService<INotificationPushSendRepository>();
            Guid? claimed = await pushSends.ClaimAsync(
                seed.RecipientNotificationId, id, _factory.Clock, default);
            Assert.NotNull(claimed);

            // Neither notification_mutes column carries an FK (shipped gap, pre-dating Phase 7), so
            // synthetic ids are enough to exercise the purge without minting real sessions/polls.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_mutes (id, user_id, session_id, created_at)
                VALUES ({Guid.CreateVersion7()}, {seed.SubjectId.Value}, {Guid.CreateVersion7()}, now())
                """);
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_mutes (id, user_id, poll_id, created_at)
                VALUES ({Guid.CreateVersion7()}, {seed.SubjectId.Value}, {Guid.CreateVersion7()}, now())
                """);
        }

        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Assert.True(await Phase3DeletionTestSupport.CreateUseCase(scope.ServiceProvider)
                .ExecuteAsync(seed.SubjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verify.PushSubscriptions.CountAsync(s => s.UserId == seed.SubjectId));
        Assert.Equal(
            0,
            await verify.NotificationPushSends.CountAsync(
                send => send.PushSubscriptionId == PushSubscriptionId.From(subscriptionId)));
        Assert.Equal(0, await verify.NotificationMutes.CountAsync(mute => mute.UserId == seed.SubjectId));
        // The shipped D-092 citation, unaffected: the notifications purge still runs alongside.
        Assert.Equal(
            0,
            await verify.Notifications.CountAsync(notification => notification.RecipientUserId == seed.SubjectId));
    }
}
