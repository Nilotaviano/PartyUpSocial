using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Polls;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Admin;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Admin;

/// <summary>
/// D-180's optional content removal end to end (PHASE8_DESIGN.md §3 S2): one admin action bans the
/// subject and runs DOMAIN_MODEL §11 steps 1–3 over a real graph, in one transaction through the
/// shared A-068 lock envelope. The exclusions are asserted as absences — the friend graph, the
/// username, the account's PII, its lifecycle, its activity column and its Completed/in-progress
/// history all survive, no journal row appears, and an unban restores none of the removed content.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContentRemovalTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RemoveContent_ProducesTheFiveEffectsAndNothingElse_D180()
    {
        RemovalSeed seed = await SeedAsync();

        AdminUserMutationResult result = await RemoveContentAsync(seed.SubjectId);

        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        // Effect 1: the subject's not-started owned session is canceled; the in-progress one is not.
        Assert.Equal(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedFutureId)).Status);
        Assert.NotEqual(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedInProgressId)).Status);

        // Effect 2: the owned live poll expires.
        Assert.Equal(
            PollStatus.Expired,
            (await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == seed.OwnedPollId)).Status);

        // Effect 3: future participation on someone else's session is removed.
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedFutureId && p.UserId == seed.SubjectId))
            .IsRemoved);

        // Effect 4: the subject's own pending join request lapses.
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.JoinRequestId)).Status);

        // Effect 5: the reminder that removal orphaned is canceled.
        Assert.Equal(0, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == seed.ParticipatedFutureId && h.RecipientUserId == seed.SubjectId &&
            h.State != SessionReminderHandoffState.Canceled));

        // The ban itself, and only the ban, on the account.
        User subject = await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == seed.SubjectId);
        Assert.NotNull(subject.BannedAt);
        Assert.False(subject.IsTombstoned);
        Assert.Equal(UserLifecycle.Active, subject.Lifecycle);
        Assert.NotNull(subject.Username);
        Assert.NotNull(subject.DisplayName);
        Assert.NotNull(subject.FriendCode);

        // Excluded: friendships survive, the username is never released, no journal row is written,
        // and last_seen_at is untouched (a banned account is still a live account, unlike a tombstone).
        Assert.Equal(
            1,
            await verify.Friendships.CountAsync(f =>
                f.UserLoId == seed.SubjectId || f.UserHiId == seed.SubjectId));
        Assert.Empty(await verify.UsernameRegistry.AsNoTracking()
            .Where(entry => entry.OwnerId == null)
            .ToListAsync());
        Assert.DoesNotContain(
            await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default),
            entry => entry.UserId == seed.SubjectId);
        Assert.Equal(seed.SeenAt, await LastSeenAsync(verify, seed.SubjectId));
        Assert.NotNull(await verification.ServiceProvider.GetRequiredService<IUserAccountGateway>()
            .GetEmailAsync(seed.SubjectId, default));
    }

    [Fact]
    public async Task RemoveContent_IsIdempotentAndUnbanRestoresNothing_D180()
    {
        RemovalSeed seed = await SeedAsync();

        Assert.True((await RemoveContentAsync(seed.SubjectId)).Succeeded);
        Assert.True((await RemoveContentAsync(seed.SubjectId)).Succeeded);

        await using (AsyncServiceScope unbanScope = _factory.Services.CreateAsyncScope())
        {
            AdminUserMutationResult unban = await unbanScope.ServiceProvider
                .GetRequiredService<UnbanUserUseCase>()
                .ExecuteAsync(seed.SubjectId, default);
            Assert.True(unban.Succeeded, unban.Code);
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Null((await verify.DomainUsers.AsNoTracking()
            .SingleAsync(u => u.Id == seed.SubjectId)).BannedAt);
        Assert.Equal(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == seed.OwnedFutureId)).Status);
        Assert.Equal(
            PollStatus.Expired,
            (await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == seed.OwnedPollId)).Status);
        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == seed.ParticipatedFutureId && p.UserId == seed.SubjectId))
            .IsRemoved);
        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == seed.JoinRequestId)).Status);
    }

    private static Task<Instant?> LastSeenAsync(AppDbContext context, UserId subjectId) =>
        context.DomainUsers.AsNoTracking()
            .Where(user => user.Id == subjectId)
            .Select(user => EF.Property<Instant?>(user, UserActivityWriter.LastSeenProperty))
            .SingleAsync();

    private async Task<AdminUserMutationResult> RemoveContentAsync(UserId subjectId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BanAndRemoveContentUseCase>()
            .ExecuteAsync(subjectId, default);
    }

    private async Task<RemovalSeed> SeedAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        UserId subjectId = await _factory.SeedUserAsync(email: $"removal-subject-{suffix}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"removal-friend-{suffix}@example.test");
        UserId hostId = await _factory.SeedUserAsync(email: $"removal-host-{suffix}@example.test");
        UserId requestHostId = await _factory.SeedUserAsync(email: $"removal-reqhost-{suffix}@example.test");

        Instant seenAt = _factory.Clock.GetCurrentInstant();
        SessionId ownedFutureId;
        SessionId ownedInProgressId;
        SessionId participatedFutureId;
        JoinRequestId joinRequestId;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, subjectId, friendId, _factory.Clock);

            Session ownedFuture = Session.Create(subjectId, Details("Owned future", 180), [], _factory.Clock);
            _ = ownedFuture.DequeueEvents();
            ownedFutureId = ownedFuture.Id;

            Session ownedInProgress = Session.Create(
                subjectId, Details("Owned in-progress", 30, durationMinutes: 120), [], _factory.Clock);
            _ = ownedInProgress.DequeueEvents();
            ownedInProgressId = ownedInProgress.Id;

            Session participatedFuture = Session.Create(
                hostId, Details("Other's future", 180), [], _factory.Clock);
            participatedFuture.Invite(subjectId, _factory.Clock);
            participatedFuture.Rsvp(
                ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            _ = participatedFuture.DequeueEvents();
            participatedFutureId = participatedFuture.Id;

            Session requestTarget = Session.Create(
                requestHostId, Details("Request target", 180), [], _factory.Clock);
            joinRequestId = requestTarget.OpenJoinRequest(subjectId, _factory.Clock).Id;
            _ = requestTarget.DequeueEvents();

            context.Sessions.AddRange(
                ownedFuture, ownedInProgress, participatedFuture, requestTarget);
            context.SessionReminderHandoffs.Add(new SessionReminderHandoff
            {
                Id = Guid.CreateVersion7(),
                SessionId = participatedFutureId,
                RecipientUserId = subjectId,
                DueAt = _factory.Clock.GetCurrentInstant() + Duration.FromHours(2),
                State = SessionReminderHandoffState.Pending,
                PlannedAt = _factory.Clock.GetCurrentInstant(),
                DeliveredAt = null,
                Version = 0,
                Attempts = 0,
                LastAttemptAt = null,
            });
            await context.SaveChangesAsync();

            // A banned account stays live, so the activity column must survive the removal.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE users SET last_seen_at = {seenAt}
                WHERE id = {subjectId.Value}
                """);
        }

        PollId ownedPollId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
                new CreatePollCommand(
                    subjectId,
                    "Owned poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    IsPrivate: false,
                    OptionStartTimes: [now + Duration.FromDays(6), now + Duration.FromDays(7)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [friendId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            ownedPollId = created.Poll!.Id;
        }

        // "Future" and "in progress" are both evaluated at the removal instant: the +30-minute
        // session has started (and its 120-minute run has not ended) while the +180-minute ones have
        // not, without any session ever being created in the past.
        _factory.Clock.Advance(Duration.FromMinutes(60));

        return new RemovalSeed(
            subjectId,
            ownedFutureId,
            ownedInProgressId,
            participatedFutureId,
            joinRequestId,
            ownedPollId,
            seenAt);
    }

    private SessionDetails Details(string title, int leadMinutes, int durationMinutes = 60) => new(
        SessionTitle.Create(title),
        _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
        Duration15.FromMinutes(durationMinutes),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        isPrivate: true);

    private sealed record RemovalSeed(
        UserId SubjectId,
        SessionId OwnedFutureId,
        SessionId OwnedInProgressId,
        SessionId ParticipatedFutureId,
        JoinRequestId JoinRequestId,
        PollId OwnedPollId,
        Instant SeenAt);
}
