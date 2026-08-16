using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// PHASE4_DESIGN.md §6.2's deletion pre-lock/reload protocol, widened by S2a/S2b to session scope:
/// D-092's future-only removal/cancellation/lapse outcomes over a real session graph, and the bounded
/// outer retry that <c>ExecuteDeletionForUserUseCase</c> runs when the candidate set discovery locked
/// differs from what reload recomputes. Reuses <c>Phase3DeletionTestSupport.CreateUseCase</c>
/// (<c>Friends/DeletionSweepTests.cs</c>) for real DI-resolved ports; that helper predates the session
/// scope, so this file seeds its own session graph rather than the Friends-only scenario it builds.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionDeletionEffectsTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task FutureOnlyRemovalOwnedCancellationAndOwnJoinRequestLapse_D092()
    {
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "deletion-effects-subject@example.test");
        UserId otherOrganizerFutureId = await _factory.SeedUserAsync(
            email: "deletion-effects-other-future@example.test");
        UserId otherOrganizerInProgressId = await _factory.SeedUserAsync(
            email: "deletion-effects-other-inprogress@example.test");
        UserId requestOrganizerId = await _factory.SeedUserAsync(
            email: "deletion-effects-request-organizer@example.test");

        SessionId ownedFutureId;
        SessionId ownedInProgressId;
        SessionId participatesFutureId;
        SessionId participatesInProgressId;
        JoinRequestId pendingRequestId;

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Deletion only becomes executable once the 7-day grace period elapses (D-148), so "future"
            // here means still-future *after* that whole week passes, and "in-progress" means started
            // (but not ended) at that same later instant — not at session-creation time.
            const int futureLeadMinutes = (7 * 24 * 60) + 180;
            const int inProgressLeadMinutes = (7 * 24 * 60) + 10;
            const int inProgressDurationMinutes = 120;

            // 1. The subject's own future (not-started) session: canceled by deletion.
            Session ownedFuture = Session.Create(
                subjectId, SessionDetailsFor(leadMinutes: futureLeadMinutes), [], _factory.Clock);
            _ = ownedFuture.DequeueEvents();
            ownedFutureId = ownedFuture.Id;

            // 2. The subject's own in-progress session: runs to completion, never canceled by deletion
            //    (§12-35).
            Session ownedInProgress = Session.Create(
                subjectId,
                SessionDetailsFor(leadMinutes: inProgressLeadMinutes, durationMinutes: inProgressDurationMinutes),
                [],
                _factory.Clock);
            _ = ownedInProgress.DequeueEvents();
            ownedInProgressId = ownedInProgress.Id;

            // 3. A future session organized by someone else, where the subject participates: their
            //    participation is removed and the reminder canceled.
            Session participatesFuture = Session.Create(
                otherOrganizerFutureId, SessionDetailsFor(leadMinutes: futureLeadMinutes), [], _factory.Clock);
            participatesFuture.Invite(subjectId, _factory.Clock);
            participatesFuture.Rsvp(ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            _ = participatesFuture.DequeueEvents();
            participatesFutureId = participatesFuture.Id;

            // 4. An in-progress session organized by someone else, where the subject participates:
            //    their participation stays untouched — it completes in place.
            Session participatesInProgress = Session.Create(
                otherOrganizerInProgressId,
                SessionDetailsFor(leadMinutes: inProgressLeadMinutes, durationMinutes: inProgressDurationMinutes),
                [],
                _factory.Clock);
            participatesInProgress.Invite(subjectId, _factory.Clock);
            participatesInProgress.Rsvp(
                ParticipantRef.ForUser(subjectId), ParticipationStatus.Going, _factory.Clock);
            _ = participatesInProgress.DequeueEvents();
            participatesInProgressId = participatesInProgress.Id;

            // 5. The subject's own pending join request on someone else's session: lapses regardless of
            //    timing (DeletionExecutor step 3 runs LapseJoinRequestsFrom unconditionally).
            Session requestTarget = Session.Create(
                requestOrganizerId, SessionDetailsFor(leadMinutes: futureLeadMinutes), [], _factory.Clock);
            JoinRequest pendingRequest = requestTarget.OpenJoinRequest(subjectId, _factory.Clock);
            _ = requestTarget.DequeueEvents();
            pendingRequestId = pendingRequest.Id;

            context.Sessions.AddRange(
                ownedFuture, ownedInProgress, participatesFuture, participatesInProgress, requestTarget);
            await context.SaveChangesAsync();
        }

        // One combined advance: past the grace period (so deletion becomes executable) and, at that
        // same later instant, past ownedInProgress/participatesInProgress's start but not their end
        // (in-progress), while still short of ownedFuture/participatesFuture's start (still future).
        _factory.Clock.Advance(User.DeletionGracePeriod + Duration.FromMinutes(30));

        await using (AsyncServiceScope deleteScope = _factory.Services.CreateAsyncScope())
        {
            ExecuteDeletionForUserUseCase useCase = Phase3DeletionSessionTestSupport.CreateUseCase(
                deleteScope.ServiceProvider);
            Assert.True(await useCase.ExecuteAsync(subjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(
            SessionStatus.Canceled,
            (await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == ownedFutureId)).Status);

        Session inProgressOwned = await verify.Sessions.AsNoTracking().SingleAsync(s => s.Id == ownedInProgressId);
        Assert.NotEqual(SessionStatus.Canceled, inProgressOwned.Status);

        Assert.True((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == participatesFutureId && p.UserId == subjectId)).IsRemoved);
        Assert.Equal(0, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == participatesFutureId && h.RecipientUserId == subjectId &&
            h.State != SessionReminderHandoffState.Canceled));

        Assert.False((await verify.SessionParticipations.AsNoTracking()
            .SingleAsync(p => p.SessionId == participatesInProgressId && p.UserId == subjectId)).IsRemoved);

        Assert.Equal(
            JoinRequestStatus.Lapsed,
            (await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == pendingRequestId)).Status);

        Assert.True((await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == subjectId)).IsTombstoned);
    }

    /// <summary>
    /// D-092 + §6.2's bounded outer retry, in both directions the ticket requires: (a) a candidate set
    /// that changes once between discovery and reload retries exactly once more and succeeds, with the
    /// first (poisoned) attempt writing nothing; (b) a candidate set that keeps changing every attempt
    /// exhausts at exactly 3 attempts, returns false, and leaves the subject untouched — bounded, not an
    /// unbounded retry loop.
    /// </summary>
    [Fact]
    public async Task ChangedCandidateSetRetriesWithoutFirstAttemptWritesAndIsBounded_D092()
    {
        // (a) succeeds on the second attempt.
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "deletion-retry-subject@example.test");
        _factory.Clock.Advance(User.DeletionGracePeriod);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var counter = new DiscoveryCallCounter();
            var decorated = new ChangedSetOnceDeletionScopeProvider(
                scope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
                scope.ServiceProvider,
                _factory.Clock,
                counter);
            ExecuteDeletionForUserUseCase useCase = Phase3DeletionSessionTestSupport.CreateUseCase(
                scope.ServiceProvider,
                scopeProvider: decorated);

            bool executed = await useCase.ExecuteAsync(subjectId, default);

            Assert.True(executed);
            Assert.Equal(2, counter.Calls);
        }

        await using (AsyncServiceScope verification = _factory.Services.CreateAsyncScope())
        {
            AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True((await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == subjectId)).IsTombstoned);
            IReadOnlyList<DeletionJournalEntry> journal = await verification.ServiceProvider
                .GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 1000, default);
            // Exactly one journal row: the poisoned first attempt wrote nothing, so there is no
            // duplicate/stale entry from it.
            Assert.Single(journal, entry => entry.UserId == subjectId);
        }

        // (b) exhausts at exactly 3 attempts and leaves the subject untouched.
        UserId exhaustedSubjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "deletion-retry-exhausted@example.test");
        _factory.Clock.Advance(User.DeletionGracePeriod);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var counter = new DiscoveryCallCounter();
            var alwaysChanging = new AlwaysChangingDeletionScopeProvider(
                scope.ServiceProvider.GetRequiredService<IDeletionScopeProvider>(),
                scope.ServiceProvider,
                _factory.Clock,
                counter);
            ExecuteDeletionForUserUseCase useCase = Phase3DeletionSessionTestSupport.CreateUseCase(
                scope.ServiceProvider,
                scopeProvider: alwaysChanging);

            bool executed = await useCase.ExecuteAsync(exhaustedSubjectId, default);

            Assert.False(executed);
            Assert.Equal(3, counter.Calls);
        }

        await using (AsyncServiceScope verification = _factory.Services.CreateAsyncScope())
        {
            AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
            User untouched = await verify.DomainUsers.AsNoTracking().SingleAsync(u => u.Id == exhaustedSubjectId);
            Assert.False(untouched.IsTombstoned);
            Assert.True(untouched.IsPendingDeletion);
        }
    }

    private SessionDetails SessionDetailsFor(int leadMinutes, int durationMinutes = 60) => new(
        SessionTitle.Create("Deletion effects session"),
        _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
        Duration15.FromMinutes(durationMinutes),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: null,
        visibility: ContentVisibility.Private);
}

internal sealed class DiscoveryCallCounter
{
    private int _calls;

    internal int Calls => Volatile.Read(ref _calls);

    internal void RecordCall() => Interlocked.Increment(ref _calls);
}

/// <summary>
/// Simulates a genuine concurrent mutation landing between discovery (no locks held) and reload: on
/// its first call, after delegating to the real discovery, it commits a brand-new session — organized
/// by a fresh third party, with the subject as a Going participant — through a separate scope/context,
/// then returns the ORIGINAL (now-stale) discovered set. The real <c>LoadForUpdateAsync</c> reload that
/// follows sees the new row and recomputes a set that includes the new organizer, which no longer
/// matches — forcing exactly the §6.2 <c>DeletionScopeChangedException</c> path. Every call after the
/// first delegates straight through, so the second attempt's discovery and reload agree and the
/// operation succeeds.
/// </summary>
internal sealed class ChangedSetOnceDeletionScopeProvider(
    IDeletionScopeProvider inner,
    IServiceProvider services,
    MutableClock clock,
    DiscoveryCallCounter counter) : IDeletionScopeProvider
{
    private int _calls;

    public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
        inner.LoadAsync(id, cancellationToken);

    public Task<DeletionScope> LoadForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        inner.LoadForUpdateAsync(id, cancellationToken);

    public async Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        counter.RecordCall();
        int call = Interlocked.Increment(ref _calls);
        IReadOnlyCollection<UserId> discovered = await inner.DiscoverAffectedUserIdsAsync(id, cancellationToken);

        if (call == 1)
        {
            await using AsyncServiceScope mutationScope = services
                .GetRequiredService<IServiceScopeFactory>()
                .CreateAsyncScope();
            AppDbContext context = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            User surpriseOrganizer = User.BeginOnboarding(
                UserId.New(),
                FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
                AgeGateSource.SelfAttested,
                clock);
            UserId surpriseOrganizerId = surpriseOrganizer.Id;
            string suffix = surpriseOrganizerId.Value.ToString("N")[..8];
            surpriseOrganizer.CompleteOnboarding(
                Username.Create($"surprise{suffix}"),
                DisplayName.Create("Surprise Organizer"),
                Avatar.FromEmoji("🙂"),
                DateTimeZoneProviders.Tzdb["America/Fortaleza"],
                Locale.Create("en"),
                clock);
            context.DomainUsers.Add(surpriseOrganizer);

            SessionDetails details = new(
                SessionTitle.Create("Concurrently-added session"),
                clock.GetCurrentInstant() + Duration.FromMinutes(120),
                Duration15.FromMinutes(60),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                visibility: ContentVisibility.Private);
            Session surpriseSession = Session.Create(surpriseOrganizerId, details, [], clock);
            surpriseSession.Invite(id, clock);
            surpriseSession.Rsvp(ParticipantRef.ForUser(id), ParticipationStatus.Going, clock);
            _ = surpriseSession.DequeueEvents();
            context.Sessions.Add(surpriseSession);

            await context.SaveChangesAsync(cancellationToken);
        }

        return discovered;
    }
}

/// <summary>Always returns a candidate set that omits a freshly (and permanently) added counterpart,
/// so discovery and reload never agree — the §6.2 bounded-exhaustion path.</summary>
internal sealed class AlwaysChangingDeletionScopeProvider(
    IDeletionScopeProvider inner,
    IServiceProvider services,
    MutableClock clock,
    DiscoveryCallCounter counter) : IDeletionScopeProvider
{
    public Task<DeletionScope> LoadAsync(UserId id, CancellationToken cancellationToken) =>
        inner.LoadAsync(id, cancellationToken);

    public Task<DeletionScope> LoadForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        inner.LoadForUpdateAsync(id, cancellationToken);

    public async Task<IReadOnlyCollection<UserId>> DiscoverAffectedUserIdsAsync(
        UserId id,
        CancellationToken cancellationToken)
    {
        counter.RecordCall();
        IReadOnlyCollection<UserId> discovered = await inner.DiscoverAffectedUserIdsAsync(id, cancellationToken);

        await using AsyncServiceScope mutationScope = services
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();
        AppDbContext context = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        User newOrganizer = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            clock);
        string suffix = newOrganizer.Id.Value.ToString("N")[..8];
        newOrganizer.CompleteOnboarding(
            Username.Create($"always{suffix}"),
            DisplayName.Create("Always Organizer"),
            Avatar.FromEmoji("🙂"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        context.DomainUsers.Add(newOrganizer);

        SessionDetails details = new(
            SessionTitle.Create("Always-changing session"),
            clock.GetCurrentInstant() + Duration.FromMinutes(120),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);
        Session session = Session.Create(newOrganizer.Id, details, [], clock);
        session.Invite(id, clock);
        session.Rsvp(ParticipantRef.ForUser(id), ParticipationStatus.Going, clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync(cancellationToken);

        return discovered;
    }
}

internal static class Phase3DeletionSessionTestSupport
{
    internal static ExecuteDeletionForUserUseCase CreateUseCase(
        IServiceProvider services,
        IDeletionScopeProvider? scopeProvider = null) =>
        new(
            services.GetRequiredService<IUserRepository>(),
            scopeProvider ?? services.GetRequiredService<IDeletionScopeProvider>(),
            services.GetRequiredService<IDeletionResultApplier>(),
            services.GetRequiredService<IUserAccountGateway>(),
            services.GetRequiredService<IDeletionJournal>(),
            new NoOpSessionDeletionJournalShipper(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<ISessionLockRepository>(),
            services.GetRequiredService<ISessionNotificationMaterializer>());
}

internal sealed class NoOpSessionDeletionJournalShipper : IDeletionJournalShipper
{
    public Task<int> ShipPendingAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}
