using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>CreatePoll (PHASE6_DESIGN.md §2 "Create"): D-125's non-private-needs-a-friend rule and the happy path.</summary>
[Trait("Category", "Integration")]
public sealed class PollCreateTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task CreatePoll_HappyPath_CreatesOrganizerParticipantAndInviteeNotification()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-create-organizer@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: "poll-create-invitee@example.test");

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeId, _factory.Clock);
            await context.SaveChangesAsync();
        }

        Instant now = _factory.Clock.GetCurrentInstant();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollResult result = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Saturday raid",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: false,
                OptionStartTimes: [now + Duration.FromHours(5), now + Duration.FromHours(6)],
                DeadlineChoice: null,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);

        Assert.True(result.Succeeded, result.Code);
        Poll poll = result.Poll!;
        Assert.Equal(2, poll.Options.Count);
        Assert.Equal(PollDeadlineChoice.Hours24, poll.DeadlineChoice);
        Assert.Equal(now + Duration.FromHours(24), poll.Deadline);
        Assert.Contains(poll.Participants, p => p.UserId == organizerId);
        Assert.Contains(poll.Participants, p => p.UserId == inviteeId);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await verifyContext.Notifications.AnyAsync(n =>
            n.PollId == poll.Id && n.RecipientUserId == inviteeId && n.Type == NotificationType.PollInvitation));
    }

    /// <summary>D-125: a non-private poll with zero directly invited registered friends is rejected.</summary>
    [Fact]
    public async Task CreatePoll_NonPrivateWithNoInvitees_Rejected_D125()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-create-nofriend-organizer@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollResult result = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "No friends invited",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: false,
                OptionStartTimes: [now + Duration.FromHours(5), now + Duration.FromHours(6)],
                DeadlineChoice: null,
                InviteeUserIds: [],
                GuestLinkCount: null),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_validation", result.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyContext.Polls.CountAsync());
    }

    /// <summary>A private poll may be created with zero registered invitees and its guest links minted in the same transaction (spec §7.3).</summary>
    [Fact]
    public async Task CreatePoll_PrivateWithGuestLinksOnly_Succeeds()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-create-private-organizer@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollResult result = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Private guest-only poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: true,
                OptionStartTimes: [now + Duration.FromHours(5), now + Duration.FromHours(6)],
                DeadlineChoice: null,
                InviteeUserIds: [],
                GuestLinkCount: 2),
            default);

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(2, result.GuestLinks!.Count);
        Assert.Equal(2, result.Poll!.GuestLinks.Count);
    }

    /// <summary>D-080/D-131: identical instants merge on the way in; fewer than two distinct options after dedupe is rejected.</summary>
    [Fact]
    public async Task CreatePoll_DedupesIdenticalInstants_AndRejectsBelowMinimum()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "poll-create-dedupe-organizer@example.test");
        Instant now = _factory.Clock.GetCurrentInstant();
        Instant startTime = now + Duration.FromHours(5);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollResult result = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Dedupe test",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: true,
                OptionStartTimes: [startTime, startTime],
                DeadlineChoice: null,
                InviteeUserIds: [],
                GuestLinkCount: null),
            default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_validation", result.Code);
    }
}
