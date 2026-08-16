using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>SuggestOption/DeleteOption (PHASE6_DESIGN.md §2 §6.2): merge-or-cap-or-insert under the poll parent lock (D-171), and organizer-only silent delete (D-080/D-088).</summary>
[Trait("Category", "Integration")]
public sealed class PollOptionTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SuggestOption_IdenticalInstant_MergesWithoutError()
    {
        (PollId pollId, UserId organizerId, UserId voterId, Instant existing) = await SeedPollWithVoterAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SuggestOptionResult result = await PollTestHelpers.BuildOptionUseCases(scope)
            .SuggestOptionAsync(pollId, voterId, existing, default);

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(2, result.Poll!.Options.Count);
    }

    /// <summary>
    /// D-171: two concurrent suggesters race to fill the twelfth and thirteenth distinct option slot.
    /// The poll parent lock (P3-LK-01) serializes them: exactly one succeeds at 12 options total and
    /// the other observes the cap and fails with <c>poll_option_cap</c> — never both succeeding past
    /// the D-080 ceiling.
    /// </summary>
    [Fact]
    public async Task SuggestOption_ConcurrentRaceAtTwelveOptionCap_ExactlyOneSucceeds_D171()
    {
        (PollId pollId, UserId organizerId, UserId voterA, Instant baseTime) = await SeedPollWithVoterAsync();
        UserId voterB = await _factory.SeedUserAsync(email: "poll-option-race-b@example.test");

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, voterB, _factory.Clock);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope approveScope = _factory.Services.CreateAsyncScope())
        {
            var joinRequests = PollTestHelpers.BuildJoinRequestUseCases(approveScope);
            PollJoinRequestCommandResult requestResult = await joinRequests.RequestJoinAsync(pollId, voterB, default);
            Assert.True(requestResult.Succeeded, requestResult.Code);
            PollJoinRequestCommandResult approveResult =
                await joinRequests.ApproveJoinRequestAsync(pollId, organizerId, requestResult.JoinRequest!.Id, default);
            Assert.True(approveResult.Succeeded, approveResult.Code);
        }

        // Fill 9 more distinct options sequentially (not part of the race), bringing the poll from 2
        // to 11 options -- one short of the D-080 cap, with room for exactly one more distinct
        // suggestion to reach 12.
        for (int i = 0; i < 9; i++)
        {
            await using AsyncServiceScope fillScope = _factory.Services.CreateAsyncScope();
            SuggestOptionResult fillResult = await PollTestHelpers.BuildOptionUseCases(fillScope)
                .SuggestOptionAsync(pollId, voterA, baseTime + Duration.FromHours(i + 2), default);
            Assert.True(fillResult.Succeeded, fillResult.Code);
        }

        await using AsyncServiceScope raceScopeA = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope raceScopeB = _factory.Services.CreateAsyncScope();

        Task<SuggestOptionResult> suggestA = PollTestHelpers.BuildOptionUseCases(raceScopeA)
            .SuggestOptionAsync(pollId, voterA, baseTime + Duration.FromHours(50), default);
        Task<SuggestOptionResult> suggestB = PollTestHelpers.BuildOptionUseCases(raceScopeB)
            .SuggestOptionAsync(pollId, voterB, baseTime + Duration.FromHours(51), default);

        SuggestOptionResult[] results = await Task.WhenAll(suggestA, suggestB);

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Equal(1, results.Count(r => !r.Succeeded && r.Code == "poll_option_cap"));

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        int optionCount = await verifyContext.Set<PollOption>().CountAsync(o => o.PollId == pollId);
        Assert.Equal(Poll.MaximumOptions, optionCount);
    }

    [Fact]
    public async Task DeleteOption_ByOrganizer_CascadesVotesWithoutRecomputingFirstResponseAt_D172()
    {
        (PollId pollId, UserId organizerId, UserId voterId, Instant startA) = await SeedPollWithVoterAsync();

        PollOptionId optionAId;
        PollOptionId optionBId;
        await using (AsyncServiceScope readScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            List<PollOption> options = await context.Set<PollOption>()
                .Where(o => o.PollId == pollId).OrderBy(o => o.StartTime).ToListAsync();
            optionAId = options[0].Id;
            optionBId = options[1].Id;
        }

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult voteResult = await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, voterId, optionAId, VoteChoice.Available, default);
            Assert.True(voteResult.Succeeded, voteResult.Code);
        }

        Instant firstResponseBefore;
        await using (AsyncServiceScope readScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            firstResponseBefore = (await context.Set<PollParticipant>()
                .SingleAsync(p => p.PollId == pollId && p.UserId == voterId)).FirstResponseAt!.Value;
        }

        await using (AsyncServiceScope deleteScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult deleteResult = await PollTestHelpers.BuildOptionUseCases(deleteScope)
                .DeleteOptionAsync(pollId, organizerId, optionAId, default);
            Assert.True(deleteResult.Succeeded, deleteResult.Code);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyContext.Set<PollOption>().CountAsync(o => o.Id == optionAId));
        Assert.Equal(0, await verifyContext.Set<PollVote>().CountAsync(v => v.OptionId == optionAId));
        Instant firstResponseAfter = (await verifyContext.Set<PollParticipant>()
            .SingleAsync(p => p.PollId == pollId && p.UserId == voterId)).FirstResponseAt!.Value;
        Assert.Equal(firstResponseBefore, firstResponseAfter);
    }

    /// <summary>
    /// §10 recipients through the WIRED materializer, not a hand-rolled call to
    /// <see cref="RecipientComputer.OptionSuggested"/>: a member suggesting produces exactly one
    /// notification row addressed to the organizer.
    /// </summary>
    [Fact]
    public async Task SuggestOption_ByMember_NotifiesOnlyTheOrganizer_D120()
    {
        (PollId pollId, UserId organizerId, UserId voterId, Instant baseTime) = await SeedPollWithVoterAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SuggestOptionResult result = await PollTestHelpers.BuildOptionUseCases(scope)
            .SuggestOptionAsync(pollId, voterId, baseTime + Duration.FromHours(5), default);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Notification notification = await verifyContext.Notifications
            .SingleAsync(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSuggested);
        Assert.Equal(organizerId, notification.RecipientUserId);
    }

    /// <summary>§10: the organizer suggesting their own option is silent -- no notification row at all.</summary>
    [Fact]
    public async Task SuggestOption_ByOrganizer_IsSilent_D120()
    {
        (PollId pollId, UserId organizerId, UserId _, Instant baseTime) = await SeedPollWithVoterAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SuggestOptionResult result = await PollTestHelpers.BuildOptionUseCases(scope)
            .SuggestOptionAsync(pollId, organizerId, baseTime + Duration.FromHours(5), default);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyContext.Notifications
            .CountAsync(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSuggested));
    }

    private async Task<(PollId PollId, UserId OrganizerId, UserId VoterId, Instant BaseStartTime)> SeedPollWithVoterAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-option-organizer-{Guid.NewGuid():N}@example.test");
        UserId voterId = await _factory.SeedUserAsync(email: $"poll-option-voter-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, voterId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        Instant baseTime = now + Duration.FromHours(2);

        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
            new PartyUp.Application.Polls.CreatePollCommand(
                organizerId,
                "Option test poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Public,
                AudienceGroupIds: null,
                OptionStartTimes: [baseTime, baseTime + Duration.FromHours(1)],
                DeadlineChoice: PollDeadlineChoice.Hours48,
                InviteeUserIds: [voterId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId, voterId, baseTime);
    }
}
