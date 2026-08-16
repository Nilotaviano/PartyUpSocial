using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Admin;

/// <summary>
/// D-179's per-user stats against a real database (PHASE8_DESIGN.md §3 S2): the reader's correlated
/// counts and the two instants PRODUCT_SPEC §10 asks for. The mapping from record to projection —
/// including the email masking — is proven by <c>AdminUseCaseTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminUserStatsTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Read_ReportsRegisteredDateLastActivityAndTheThreeCounts_D179()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId subjectId = await _factory.SeedUserAsync(email: $"stats-subject-{suffix}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"stats-friend-{suffix}@example.test");
        UserId hostId = await _factory.SeedUserAsync(email: $"stats-host-{suffix}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"stats-stranger-{suffix}@example.test");
        Instant seenAt = _factory.Clock.GetCurrentInstant();

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await Polls.PollTestHelpers.SeedAcceptedFriendshipAsync(
                context, subjectId, friendId, _factory.Clock);

            // Two organized sessions.
            Session organizedOne = Session.Create(subjectId, Details("Organized one"), [], _factory.Clock);
            Session organizedTwo = Session.Create(subjectId, Details("Organized two"), [], _factory.Clock);

            // One joined session, plus one the subject was removed from (never counted as joined).
            Session joined = Session.Create(hostId, Details("Joined"), [], _factory.Clock);
            joined.Invite(subjectId, _factory.Clock);
            joined.Rsvp(ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);

            Session removedFrom = Session.Create(strangerId, Details("Removed from"), [], _factory.Clock);
            removedFrom.Invite(subjectId, _factory.Clock);
            removedFrom.Rsvp(ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            removedFrom.RemoveParticipation(ParticipantRef.ForUser(subjectId), _factory.Clock);

            foreach (Session session in new[] { organizedOne, organizedTwo, joined, removedFrom })
            {
                _ = session.DequeueEvents();
            }

            context.Sessions.AddRange(organizedOne, organizedTwo, joined, removedFrom);
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET last_seen_at = {seenAt} WHERE id = {subjectId.Value}
                """);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        IAdminUserReader reader = verification.ServiceProvider.GetRequiredService<IAdminUserReader>();

        AdminUserRecord? record = await reader.GetAsync(subjectId, default);

        Assert.NotNull(record);
        Assert.Equal(2, record.SessionsOrganized);
        Assert.Equal(1, record.SessionsJoined);
        Assert.Equal(1, record.FriendCount);
        Assert.Equal(seenAt, record.LastSeenAt);
        Assert.NotEqual(default, record.CreatedAt);

        // The same numbers reach the list projection, and a never-seen account reports no activity.
        AdminUserPage page = await reader.ListAsync($"stats-subject-{suffix}", 1, 10, default);
        AdminUserRecord listed = Assert.Single(page.Users);
        Assert.Equal(record, listed);

        AdminUserRecord? stranger = await reader.GetAsync(strangerId, default);
        Assert.NotNull(stranger);
        Assert.Null(stranger.LastSeenAt);
        Assert.Equal(1, stranger.SessionsOrganized);
        Assert.Equal(0, stranger.SessionsJoined);
        Assert.Equal(0, stranger.FriendCount);
    }

    private SessionDetails Details(string title) => new(
        SessionTitle.Create(title),
        _factory.Clock.GetCurrentInstant() + Duration.FromDays(2),
        Duration15.FromMinutes(60),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: ContentVisibility.Private);
}
