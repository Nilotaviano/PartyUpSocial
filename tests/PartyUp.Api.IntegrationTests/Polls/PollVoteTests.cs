using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>Vote/revise (PHASE6_DESIGN.md §2 §6.3): D-079's deadline guard and D-172's write-once FirstResponseAt under an upsert race.</summary>
[Trait("Category", "Integration")]
public sealed class PollVoteTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Vote_BeforeDeadline_Succeeds_D079()
    {
        (PollId pollId, UserId voterId, PollOptionId optionId) = await SeedPollWithVoterAndOptionAsync(PollDeadlineChoice.Hours24);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildVoteUseCases(scope)
            .VoteAsync(pollId, voterId, optionId, VoteChoice.Available, default);

        Assert.True(result.Succeeded, result.Code);
    }

    [Fact]
    public async Task Vote_AfterDeadline_RejectedAsVotingClosed_D079()
    {
        (PollId pollId, UserId voterId, PollOptionId optionId) = await SeedPollWithVoterAndOptionAsync(PollDeadlineChoice.Hours6);

        _factory.Clock.Advance(Duration.FromHours(7));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildVoteUseCases(scope)
            .VoteAsync(pollId, voterId, optionId, VoteChoice.Available, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_voting_closed", result.Code);
    }

    /// <summary>
    /// D-172: FirstResponseAt is set by the first vote submission only, never recomputed by a
    /// revision. A revise-after-first-vote proves the timestamp is unchanged. A genuine two-writer
    /// upsert race (same participant, same option, from two concurrent callers) is serialized by the
    /// poll parent lock -- exactly one write establishes FirstResponseAt and the loser's revision
    /// leaves it untouched, proving D-172's write-once guarantee holds under contention, not merely
    /// sequentially.
    /// </summary>
    [Fact]
    public async Task Vote_UpsertRace_FirstResponseAtIsWriteOnce_D172()
    {
        (PollId pollId, UserId voterId, PollOptionId optionId) = await SeedPollWithVoterAndOptionAsync(PollDeadlineChoice.Hours24);

        await using AsyncServiceScope scopeA = _factory.Services.CreateAsyncScope();
        await using AsyncServiceScope scopeB = _factory.Services.CreateAsyncScope();

        Task<PollCommandResult> voteA = PollTestHelpers.BuildVoteUseCases(scopeA)
            .VoteAsync(pollId, voterId, optionId, VoteChoice.Available, default);
        Task<PollCommandResult> voteB = PollTestHelpers.BuildVoteUseCases(scopeB)
            .VoteAsync(pollId, voterId, optionId, VoteChoice.Maybe, default);

        PollCommandResult[] results = await Task.WhenAll(voteA, voteB);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Code));

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verifyContext.Set<PollVote>().CountAsync(v => v.PollId == pollId && v.OptionId == optionId));

        Instant firstResponseAfterRace = (await verifyContext.Set<PollParticipant>()
            .SingleAsync(p => p.PollId == pollId && p.UserId == voterId)).FirstResponseAt!.Value;

        // A third, later revision must not move FirstResponseAt.
        _factory.Clock.Advance(Duration.FromMinutes(5));
        await using (AsyncServiceScope reviseScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult reviseResult = await PollTestHelpers.BuildVoteUseCases(reviseScope)
                .VoteAsync(pollId, voterId, optionId, VoteChoice.Unavailable, default);
            Assert.True(reviseResult.Succeeded, reviseResult.Code);
        }

        Instant firstResponseAfterRevise = (await verifyContext.Set<PollParticipant>()
            .AsNoTracking()
            .SingleAsync(p => p.PollId == pollId && p.UserId == voterId)).FirstResponseAt!.Value;
        Assert.Equal(firstResponseAfterRace, firstResponseAfterRevise);
    }

    private async Task<(PollId PollId, UserId VoterId, PollOptionId OptionId)> SeedPollWithVoterAndOptionAsync(
        PollDeadlineChoice deadlineChoice)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-vote-organizer-{Guid.NewGuid():N}@example.test");
        UserId voterId = await _factory.SeedUserAsync(email: $"poll-vote-voter-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, voterId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        Instant startTime = now + Duration.FromHours(2);

        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Vote test poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: false,
                OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                DeadlineChoice: deadlineChoice,
                InviteeUserIds: [voterId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, voterId, created.Poll!.Options[0].Id);
    }
}
