using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Sessions;

/// <summary>
/// D-221/D-222: the Session-history surface. The page's membership rule (took part, terminal
/// status), the roster's masking, and the recap's arithmetic are proven here against the in-memory
/// read side; the exact keyset SQL and the visibility fragment stay the real repository's own
/// integration-test business.
/// </summary>
public sealed class SessionHistoryReadTests
{
    [Fact]
    public async Task HistoryCarriesCanceledSessionsBesideCompletedOnes_D221()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.OrganizerId;
        Session completed = SeedSession(fixture, viewer);
        Session canceled = SeedSession(fixture, viewer);
        canceled.Cancel(fixture.Clock);
        _ = canceled.DequeueEvents();
        CompleteEverythingDue(fixture);

        SessionPage<SessionSummary> history = await fixture.ReadUseCases()
            .ListCompletedHistoryAsync(viewer, null, 20, default);

        Assert.Equal(
            SessionStatus.Completed,
            history.Items.Single(s => s.Id == completed.Id).Status);
        Assert.Equal(
            SessionStatus.Canceled,
            history.Items.Single(s => s.Id == canceled.Id).Status);
    }

    /// <summary>
    /// D-221: an invitation the viewer never accepted is not their history — neither the Pending one
    /// they ignored nor the one they declined, both of which leave a participation row behind and so
    /// still resolve visible under rule 1.
    /// </summary>
    [Fact]
    public async Task HistoryExcludesSessionsTheViewerDeclinedOrLeftPending_D221()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.InviteeId;
        UserId host = fixture.OrganizerId;

        Session pending = SeedSession(fixture, host, invitees: [viewer]);
        Session declined = SeedSession(fixture, host, invitees: [viewer]);
        declined.DeclineInvitation(ParticipantRef.ForUser(viewer), fixture.Clock);
        Session attended = SeedSession(fixture, host, invitees: [viewer], going: [viewer]);
        CompleteEverythingDue(fixture);

        SessionPage<SessionSummary> history = await fixture.ReadUseCases()
            .ListCompletedHistoryAsync(viewer, null, 20, default);

        Assert.Contains(history.Items, s => s.Id == attended.Id);
        Assert.DoesNotContain(history.Items, s => s.Id == pending.Id);
        Assert.DoesNotContain(history.Items, s => s.Id == declined.Id);
    }

    /// <summary>
    /// D-221 is the deliberate narrowing of D-056's "any completed session you can see": a friend's
    /// completed session the viewer only ever had inherited access to is visible (they can still open
    /// it) but is not part of their own history.
    /// </summary>
    [Fact]
    public async Task HistoryExcludesAVisibleSessionTheViewerNeverJoined_D221_D056()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.InviteeId;
        UserId host = fixture.OrganizerId;
        fixture.Reads.MakeFriends(viewer, host);

        Session theirs = SeedSession(fixture, host, invitees: [UserId.New()]);
        Session mine = SeedSession(fixture, viewer, invitees: [host]);
        CompleteEverythingDue(fixture);

        SessionReadUseCases reads = fixture.ReadUseCases();
        Assert.NotNull(await reads.GetSessionDetailAsync(theirs.Id, viewer, default));

        SessionPage<SessionSummary> history = await reads.ListCompletedHistoryAsync(viewer, null, 20, default);

        Assert.Contains(history.Items, s => s.Id == mine.Id);
        Assert.DoesNotContain(history.Items, s => s.Id == theirs.Id);
    }

    /// <summary>
    /// D-221/D-076: the roster reads exactly like the detail page's — everyone the viewer actually
    /// played with is named, friend or not, because <c>GET /sessions/{id}</c> would name them too.
    /// Only the avatar is narrower: it needs the friend-set resolution (D-188), so a non-friend
    /// co-participant travels as a name alone. The organizer leads the list, so its order is a fact
    /// about the session rather than about however the rows were materialized.
    /// </summary>
    [Fact]
    public async Task HistoryPlayersMatchTheDetailRostersLegibility_D221()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.OrganizerId;
        UserId friend = fixture.Friendships.MakeAcceptedWith(viewer, fixture.Clock);
        UserId stranger = UserId.New();

        fixture.Reads.SetNameplate(viewer, "Vera");
        fixture.Reads.SetNameplate(friend, "Frankie", Avatar.FromEmoji("🎮"));
        fixture.Reads.SetNameplate(stranger, "Stranger", Avatar.FromEmoji("🎲"));

        Session session = SeedSession(
            fixture, viewer, invitees: [friend, stranger], going: [friend, stranger]);
        CompleteEverythingDue(fixture);

        SessionPage<SessionSummary> history = await fixture.ReadUseCases()
            .ListCompletedHistoryAsync(viewer, null, 20, default);

        IReadOnlyList<HistoryPlayer> players = history.Items.Single(s => s.Id == session.Id).Players!;
        // Organizer first is the contract; the friend/stranger tail ties on the fixture clock's
        // single instant and falls to the participation-id tie-break, so it is asserted as a set.
        Assert.Equal(viewer, players[0].UserId);
        Assert.Equal("Vera", players[0].DisplayName!.Value.Value);
        Assert.Equal(
            new HashSet<UserId> { friend, stranger },
            players.Skip(1).Select(p => p.UserId).ToHashSet());

        HistoryPlayer friendPlayer = players.Single(p => p.UserId == friend);
        Assert.Equal("Frankie", friendPlayer.DisplayName!.Value.Value);
        Assert.NotNull(friendPlayer.Avatar);

        HistoryPlayer strangerPlayer = players.Single(p => p.UserId == stranger);
        Assert.Equal("Stranger", strangerPlayer.DisplayName!.Value.Value);
        Assert.Null(strangerPlayer.Avatar);
    }

    /// <summary>D-076: a blocked pair is masked even where a friendship row still exists.</summary>
    [Fact]
    public async Task HistoryPlayersMaskABlockedPair_D221_D076()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.OrganizerId;
        UserId blocked = fixture.Friendships.MakeAcceptedWith(viewer, fixture.Clock);
        fixture.Reads.SetNameplate(blocked, "Blocked");
        fixture.Blocks.Add(Block.Create(viewer, blocked, Username.Create("blockeduser"), fixture.Clock));

        Session session = SeedSession(fixture, viewer, invitees: [blocked], going: [blocked]);
        CompleteEverythingDue(fixture);

        SessionPage<SessionSummary> history = await fixture.ReadUseCases()
            .ListCompletedHistoryAsync(viewer, null, 20, default);

        HistoryPlayer entry = history.Items
            .Single(s => s.Id == session.Id).Players!
            .Single(p => p.UserId == blocked);
        Assert.Null(entry.DisplayName);
        Assert.Null(entry.Avatar);
    }

    [Fact]
    public async Task HistoryStatsCountOnlyCompletedSessions_D222()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.OrganizerId;
        _ = SeedSession(fixture, viewer, durationMinutes: 120);
        Session canceled = SeedSession(fixture, viewer, durationMinutes: 600);
        canceled.Cancel(fixture.Clock);
        _ = canceled.DequeueEvents();
        CompleteEverythingDue(fixture);

        SessionHistoryStats stats = await fixture.ReadUseCases().GetHistoryStatsAsync(viewer, default);

        Assert.Equal(1, stats.CompletedCount);
        Assert.Equal(2, stats.HoursTogether);
    }

    [Fact]
    public async Task HistoryStatsOfAnUntouchedAccountAreZeroed_D222()
    {
        SessionTestFixture fixture = new();

        SessionHistoryStats stats = await fixture.ReadUseCases()
            .GetHistoryStatsAsync(fixture.OrganizerId, default);

        Assert.Equal(0, stats.CompletedCount);
        Assert.Equal(0, stats.HoursTogether);
        Assert.Null(stats.TopGame);
        Assert.Empty(stats.TopMates);
    }

    /// <summary>
    /// D-222/D-076: a top mate is a co-participant of every session they were counted on, so their
    /// name travels on the history roster's terms — named whether or not they are a friend, with the
    /// avatar still friend-set-gated.
    /// </summary>
    [Fact]
    public async Task HistoryStatsMatesMatchTheRostersLegibility_D222()
    {
        SessionTestFixture fixture = new();
        UserId viewer = fixture.OrganizerId;
        UserId friend = fixture.Friendships.MakeAcceptedWith(viewer, fixture.Clock);
        UserId stranger = UserId.New();
        fixture.Reads.SetNameplate(friend, "Frankie", Avatar.FromEmoji("🎮"));
        fixture.Reads.SetNameplate(stranger, "Stranger", Avatar.FromEmoji("🎲"));

        _ = SeedSession(fixture, viewer, invitees: [friend, stranger], going: [friend, stranger]);
        CompleteEverythingDue(fixture);

        SessionHistoryStats stats = await fixture.ReadUseCases().GetHistoryStatsAsync(viewer, default);

        Assert.DoesNotContain(stats.TopMates, mate => mate.Player.UserId == viewer);
        HistoryTopMate friendMate = stats.TopMates.Single(m => m.Player.UserId == friend);
        HistoryTopMate strangerMate = stats.TopMates.Single(m => m.Player.UserId == stranger);
        Assert.Equal("Frankie", friendMate.Player.DisplayName!.Value.Value);
        Assert.NotNull(friendMate.Player.Avatar);
        Assert.Equal("Stranger", strangerMate.Player.DisplayName!.Value.Value);
        Assert.Null(strangerMate.Player.Avatar);
    }

    /// <summary>
    /// D-222: minutes are summed across the whole history and only the total is rounded, half up —
    /// 90 + 90 + 45 = 225 minutes is 4 hours, where rounding each session first would say 5.
    /// </summary>
    [Fact]
    public void HistoryStatsRoundTheTotalOnceNotEachSession_D222()
    {
        SessionHistoryStats stats = SessionHistoryStatsCalculator.Compute([
            Fact(90),
            Fact(90),
            Fact(45),
        ]);

        Assert.Equal(3, stats.CompletedCount);
        Assert.Equal(4, stats.HoursTogether);
    }

    [Fact]
    public void HistoryStatsBreakTopGameTiesByNameAndIgnoreGamelessSessions_D222()
    {
        SessionHistoryStats stats = SessionHistoryStatsCalculator.Compute([
            Fact(60, game: "Overcooked"),
            Fact(60, game: "Deep Rock Galactic"),
            Fact(60),
        ]);

        Assert.Equal("Deep Rock Galactic", stats.TopGame!.Name.Value);
        Assert.Equal(1, stats.TopGame.Times);
    }

    /// <summary>D-222: three names at most, most-played first, ties broken by id — never by hash order.</summary>
    [Fact]
    public void HistoryStatsNameAtMostThreeMatesMostPlayedFirst_D222()
    {
        UserId first = UserId.From(new Guid("00000000-0000-0000-0000-0000000000a1"));
        UserId second = UserId.From(new Guid("00000000-0000-0000-0000-0000000000a2"));
        UserId third = UserId.From(new Guid("00000000-0000-0000-0000-0000000000a3"));
        UserId fourth = UserId.From(new Guid("00000000-0000-0000-0000-0000000000a4"));

        SessionHistoryStats stats = SessionHistoryStatsCalculator.Compute([
            Fact(60, companions: [fourth, third, second, first]),
            Fact(60, companions: [third, second, first]),
            Fact(60, companions: [second, first]),
            Fact(60, companions: [first]),
        ]);

        Assert.Equal([first, second, third], stats.TopMates.Select(mate => mate.Player.UserId));
        Assert.Equal([4, 3, 2], stats.TopMates.Select(mate => mate.Times));
    }

    private static CompletedSessionFact Fact(
        int durationMinutes,
        string? game = null,
        IReadOnlyList<UserId>? companions = null) =>
        new(
            SessionId.New(),
            durationMinutes,
            game is null ? null : GameName.Create(game),
            companions ?? []);

    /// <summary>
    /// Seeds a future session whose organizer is <paramref name="organizerId"/>, then turns the
    /// listed invitees into active Going participants — the D-221 attachment the history arm tests.
    /// </summary>
    private static Session SeedSession(
        SessionTestFixture fixture,
        UserId organizerId,
        IReadOnlyList<UserId>? invitees = null,
        IReadOnlyList<UserId>? going = null,
        int durationMinutes = 120)
    {
        IReadOnlyList<UserId> invited = invitees ?? [UserId.New()];
        Session session = fixture.SeedSession(
            minAttendance: 1,
            maxCapacity: null,
            durationMinutes: durationMinutes,
            invitees: invited,
            organizerId: organizerId);

        foreach (UserId attendee in going ?? [])
        {
            session.Rsvp(ParticipantRef.ForUser(attendee), ParticipationStatus.Going, fixture.Clock);
        }

        _ = session.DequeueEvents();
        return session;
    }

    /// <summary>Moves past every seeded session's end and settles each one on its materialized status.</summary>
    private static void CompleteEverythingDue(SessionTestFixture fixture)
    {
        fixture.Clock.Advance(
            SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromHours(24));

        foreach (Session session in fixture.Sessions.All)
        {
            session.ApplyDueTransitions(fixture.Clock);
            _ = session.DequeueEvents();
        }
    }
}
