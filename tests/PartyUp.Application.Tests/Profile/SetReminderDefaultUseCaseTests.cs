using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Profile;
using PartyUp.Application.Sessions;
using PartyUp.Application.Tests.Friends;
using PartyUp.Application.Tests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Profile;

/// <summary>
/// PHASE7_DESIGN.md §4's reminder-default replan (D-082 gap). Ticket-P7-S4-notes N3: proves the page
/// loop's exhaustion behavior via a recording decorator's call pattern rather than an excessive
/// &gt;100-real-session fixture, reads <see cref="RecordingReminderHandoffWriter.ReplansWithDefault"/>
/// (previously written, never read), and asserts the shipped lock order per
/// <c>SetSessionReminderUseCase.cs:35-36</c> (<c>LockUsersAsync</c> then <c>LockSessionsAsync</c>).
/// </summary>
public sealed class SetReminderDefaultUseCaseTests
{
    [Fact]
    public async Task Execute_PagesUntilAShortPageEndsIt_AndReplansEachRealSessionAgainstTheNewOffset()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 8, 1, 12, 0));
        User user = FriendTestData.Active($"u{Guid.NewGuid():N}"[..12], "Organizer");
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;

        var innerSessions = new FakeSessionRepository();
        Session first = SeedSession(innerSessions, user.Id, clock, hoursFromNow: 24);
        Session second = SeedSession(innerSessions, user.Id, clock, hoursFromNow: 48);
        Session third = SeedSession(innerSessions, user.Id, clock, hoursFromNow: 72);

        // Page 1 is exactly PageSize (100) long -- the use case's own "maybe more" signal -- but only
        // two of those hundred candidates resolve to a real session; the rest are filler ids that
        // LoadForUpdateAsync legitimately returns null for and the loop silently skips. This proves the
        // continuation branch fires without seeding 100 real Session aggregates.
        List<FutureEligibleSession> page1 = [.. Enumerable.Range(0, 98)
            .Select(_ => new FutureEligibleSession(SessionId.From(Guid.NewGuid()), clock.GetCurrentInstant() + Duration.FromDays(9)))];
        page1.Insert(0, new FutureEligibleSession(first.Id, first.StartTime));
        page1.Add(new FutureEligibleSession(second.Id, second.StartTime));
        List<FutureEligibleSession> page2 = [new FutureEligibleSession(third.Id, third.StartTime)];

        var pagingSessions = new PagingRecordingSessionRepository(innerSessions, [page1, page2]);
        var locks = new OrderedLockRepository();
        var reminders = new RecordingReminderHandoffWriter();
        var unitOfWork = new FakeUnitOfWork();

        var useCase = new SetReminderDefaultUseCase(users, pagingSessions, locks, reminders, unitOfWork, clock);

        ProfileMutationResult result = await useCase.ExecuteAsync(user.Id, ReminderOffset.Hours24, default);

        Assert.True(result.Succeeded);

        // Call pattern: exactly two page reads (100-long, then a short 1-long page that ends the loop),
        // the second keyed off the first page's last (real) candidate.
        Assert.Equal(2, pagingSessions.PageCalls.Count);
        Assert.Null(pagingSessions.PageCalls[0].AfterCursor);
        Assert.Equal(100, pagingSessions.PageCalls[0].Limit);
        Assert.Equal((second.StartTime, second.Id.Value), pagingSessions.PageCalls[1].AfterCursor);

        // Every filler candidate resolved to no session and was silently skipped; only the three real
        // sessions were replanned, each against the NEW offset via the explicit parameter (not a
        // re-read) -- ReplansWithDefault, shipped in S4 but never asserted until now.
        Assert.Equal(3, reminders.ReplansWithDefault.Count);
        Assert.All(
            reminders.ReplansWithDefault,
            call => Assert.Equal(ReminderOffset.Hours24, call.AccountDefault));
        Assert.Equal(
            new[] { first.Id, second.Id, third.Id },
            reminders.ReplansWithDefault.Select(call => call.SessionId));

        // Lock order: LockUsersAsync then LockSessionsAsync, once per PAGED candidate (100 in page 1 --
        // filler ids included, since the lock precedes the LoadForUpdateAsync null-check that skips
        // them -- plus 1 in page 2), each pair in that exact order
        // (SetSessionReminderUseCase.cs:35-36's shipped order, A-066).
        Assert.Equal(2 * (page1.Count + page2.Count), locks.Calls.Count);
        for (int i = 0; i < locks.Calls.Count; i += 2)
        {
            Assert.Equal("users", locks.Calls[i]);
            Assert.Equal("sessions", locks.Calls[i + 1]);
        }
    }

    private static Session SeedSession(FakeSessionRepository sessions, UserId organizerId, TestClock clock, int hoursFromNow)
    {
        SessionDetails details = new(
            SessionTitle.Create("Replan probe"),
            clock.GetCurrentInstant() + Duration.FromHours(hoursFromNow),
            Duration15.FromMinutes(120),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            isPrivate: true);

        Session session = Session.Create(organizerId, details, [], clock);
        _ = session.DequeueEvents();
        sessions.Add(session);
        return session;
    }
}

/// <summary>
/// Delegates every real member to <paramref name="inner"/> except the paged read, which returns the
/// next canned page from <paramref name="pages"/> and records each call's (cursor, limit) — the
/// recording-fake call-pattern idiom N3 asks for in place of a real &gt;100-session fixture.
/// </summary>
internal sealed class PagingRecordingSessionRepository(
    FakeSessionRepository inner,
    IReadOnlyList<IReadOnlyList<FutureEligibleSession>> pages) : ISessionRepository
{
    private int _callIndex;

    internal List<((Instant StartTime, Guid Id)? AfterCursor, int Limit)> PageCalls { get; } = [];

    public void Add(Session session) => inner.Add(session);

    public Task<Session?> FindAsync(SessionId id, CancellationToken cancellationToken) =>
        inner.FindAsync(id, cancellationToken);

    public Task<Session?> FindNoTrackingAsync(SessionId id, CancellationToken cancellationToken) =>
        inner.FindNoTrackingAsync(id, cancellationToken);

    public Task<SessionForUpdate?> LoadForUpdateAsync(SessionId id, CancellationToken cancellationToken) =>
        inner.LoadForUpdateAsync(id, cancellationToken);

    public Task<IReadOnlyList<FutureEligibleSession>> ListFutureSessionsWithEligibleParticipationAsync(
        UserId userId,
        Instant now,
        (Instant StartTime, Guid Id)? afterCursor,
        int limit,
        CancellationToken cancellationToken)
    {
        PageCalls.Add((afterCursor, limit));
        IReadOnlyList<FutureEligibleSession> page = _callIndex < pages.Count ? pages[_callIndex] : [];
        _callIndex++;
        return Task.FromResult(page);
    }
}

/// <summary>Records "users"/"sessions" in one combined, call-ordered log (the <c>LockOrderRecorder</c>
/// idiom <c>SessionBlockScopeTests.cs</c> already ships, generalized to one repository's two members
/// instead of two sibling repositories).</summary>
internal sealed class OrderedLockRepository : ISessionLockRepository
{
    internal List<string> Calls { get; } = [];

    public Task LockUsersAsync(IReadOnlyCollection<UserId> userIds, CancellationToken cancellationToken)
    {
        Calls.Add("users");
        return Task.CompletedTask;
    }

    public Task LockSessionsAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken)
    {
        Calls.Add("sessions");
        return Task.CompletedTask;
    }
}
