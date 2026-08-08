using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Api.IntegrationTests;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// PHASE6_DESIGN.md §1's round-trip gate: build the Poll aggregate exactly through its public API
/// (no domain edits), save it, reload it in a fresh untracked context, and confirm every child
/// collection and the D-172 write-once <see cref="PollParticipant.FirstResponseAt"/> shape survive.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private static readonly Instant Origin = Instant.FromUtc(2026, 9, 1, 12, 0);

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Poll_RoundTripsOptionsParticipantsVotesAndGuests_D172()
    {
        var clock = new MutableClock(Origin);

        await using (AppDbContext seedContext = CreateContext())
        {
            await seedContext.Database.MigrateAsync();
        }

        UserId organizerId;
        UserId inviteeId;
        Guid pollId;
        Guid firstOptionId;
        Guid guestLinkId;
        Instant firstResponseAt;

        await using (AppDbContext context = CreateContext())
        {
            organizerId = await SeedUserAsync(context, clock);
            inviteeId = await SeedUserAsync(context, clock);

            Poll poll = Poll.Create(
                organizerId,
                new PollDetails(
                    SessionTitle.Create("Raid night"),
                    Duration15.FromMinutes(120),
                    Platform.NotApplicable,
                    null,
                    SessionNotes.Empty,
                    minAttendance: 1,
                    maxCapacity: null,
                    isPrivate: false),
                PollDeadlineChoice.Hours24,
                [Origin + Duration.FromHours(72), Origin + Duration.FromHours(96)],
                [inviteeId],
                clock);

            firstOptionId = poll.Options[0].Id.Value;
            ParticipantRef inviteeRef = ParticipantRef.ForUser(inviteeId);
            ParticipantRef organizerRef = ParticipantRef.ForUser(organizerId);

            poll.Vote(inviteeRef, poll.Options[0].Id, VoteChoice.Available, clock);
            firstResponseAt = poll.FindParticipant(inviteeRef)!.FirstResponseAt!.Value;

            // D-172: a revision must not move FirstResponseAt, even after a clock advance.
            clock.Advance(Duration.FromMinutes(30));
            poll.Vote(inviteeRef, poll.Options[0].Id, VoteChoice.Maybe, clock);

            poll.Vote(organizerRef, poll.Options[1].Id, VoteChoice.Unavailable, clock);

            (GuestLink link, _) = poll.CreateGuestLink(clock);
            guestLinkId = link.Id.Value;
            poll.ClaimGuestLink(link.Id, DisplayName.Create("Guesty"), clock);

            pollId = poll.Id.Value;

            context.Polls.Add(poll);
            await context.SaveChangesAsync();
        }

        await using AppDbContext reloadContext = CreateContext();
        Poll reloaded = await reloadContext.Set<Poll>()
            .AsNoTracking()
            .Include(poll => poll.Options)
            .Include(poll => poll.Participants)
            .Include(poll => poll.Votes)
            .Include(poll => poll.GuestIdentities)
            .Include(poll => poll.GuestLinks)
            .SingleAsync(poll => poll.Id == PollId.From(pollId));

        Assert.Equal(2, reloaded.Options.Count);
        Assert.Equal(3, reloaded.Participants.Count); // organizer + invitee + claimed guest
        Assert.Equal(2, reloaded.Votes.Count); // invitee's revised vote (still one row) + organizer's vote
        Assert.Single(reloaded.GuestIdentities);
        Assert.Single(reloaded.GuestLinks);
        Assert.True(reloaded.GuestLinks[0].IsClaimed);
        Assert.Equal(guestLinkId, reloaded.GuestLinks[0].Id.Value);

        ParticipantRef reloadedInviteeRef = ParticipantRef.ForUser(inviteeId);
        PollParticipant inviteeRow = reloaded.FindParticipant(reloadedInviteeRef)!;
        Assert.Equal(firstResponseAt, inviteeRow.FirstResponseAt);

        PollVote revisedVote = reloaded.FindVote(PollOptionId.From(firstOptionId), reloadedInviteeRef)!;
        Assert.Equal(VoteChoice.Maybe, revisedVote.Choice);
    }

    private static async Task<UserId> SeedUserAsync(AppDbContext context, MutableClock clock)
    {
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create($"PU-{Guid.NewGuid():N}"[..9]),
            AgeGateSource.SelfAttested,
            clock);
        context.DomainUsers.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
