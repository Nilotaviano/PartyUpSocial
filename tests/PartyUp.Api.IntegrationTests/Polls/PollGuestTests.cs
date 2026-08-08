using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>Guest lifecycle (PHASE6_DESIGN.md §2 §5.4): mint/claim/vote, the unclaimed-vote 409, rename last-write-wins (D-126), remove guest (D-044), mint-after-close (D-079).</summary>
[Trait("Category", "Integration")]
public sealed class PollGuestTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Guest_ClaimThenVote_Succeeds()
    {
        (PollId pollId, UserId organizerId, PollOptionId optionId) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult claimResult = await PollTestHelpers.BuildGuestUseCases(claimScope)
                .ClaimGuestLinkAsync(pollId, linkId, DisplayName.Create("Guest One"), default);
            Assert.True(claimResult.Succeeded, claimResult.Code);
        }

        await using AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope();
        GuestPollCommandResult voteResult = await PollTestHelpers.BuildGuestUseCases(voteScope)
            .VoteAsGuestAsync(pollId, linkId, optionId, VoteChoice.Available, default);

        Assert.True(voteResult.Succeeded, voteResult.Code);
    }

    /// <summary>Voting before claiming is a 409-shaped application error -- claiming is explicit.</summary>
    [Fact]
    public async Task Guest_VoteBeforeClaim_ReturnsNotClaimed409()
    {
        (PollId pollId, UserId organizerId, PollOptionId optionId) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        await using AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope();
        GuestPollCommandResult voteResult = await PollTestHelpers.BuildGuestUseCases(voteScope)
            .VoteAsGuestAsync(pollId, linkId, optionId, VoteChoice.Available, default);

        Assert.False(voteResult.Succeeded);
        Assert.Equal("guest_link_not_claimed", voteResult.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verifyContext.Set<PollVote>().CountAsync(v => v.PollId == pollId));
    }

    /// <summary>D-044/D-126: renaming is last-write-wins -- the later write across "devices" is the one that sticks.</summary>
    [Fact]
    public async Task Guest_Rename_LastWriteWins_D126()
    {
        (PollId pollId, UserId organizerId, PollOptionId _) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult claimResult = await PollTestHelpers.BuildGuestUseCases(claimScope)
                .ClaimGuestLinkAsync(pollId, linkId, DisplayName.Create("First Name"), default);
            Assert.True(claimResult.Succeeded, claimResult.Code);
        }

        await using (AsyncServiceScope renameScope1 = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult renameResult = await PollTestHelpers.BuildGuestUseCases(renameScope1)
                .RenameGuestAsync(pollId, linkId, DisplayName.Create("Second Name"), default);
            Assert.True(renameResult.Succeeded, renameResult.Code);
        }

        await using (AsyncServiceScope renameScope2 = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult renameResult = await PollTestHelpers.BuildGuestUseCases(renameScope2)
                .RenameGuestAsync(pollId, linkId, DisplayName.Create("Third Name"), default);
            Assert.True(renameResult.Succeeded, renameResult.Code);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        GuestIdentity identity = await verifyContext.Set<GuestIdentity>()
            .AsNoTracking().SingleAsync(g => g.PollId == pollId);
        Assert.Equal("Third Name", identity.DisplayName.Value);
    }

    /// <summary>D-044: removing a claimed guest removes their voter row and revokes every link pointing at them, one command.</summary>
    [Fact]
    public async Task RemoveGuest_RemovesVoterAndRevokesLink_OneTransaction_D044()
    {
        (PollId pollId, UserId organizerId, PollOptionId optionId) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        GuestIdentityId guestIdentityId;
        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult claimResult = await PollTestHelpers.BuildGuestUseCases(claimScope)
                .ClaimGuestLinkAsync(pollId, linkId, DisplayName.Create("Removable Guest"), default);
            Assert.True(claimResult.Succeeded, claimResult.Code);
            guestIdentityId = claimResult.GuestIdentityId!.Value;
        }

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult voteResult = await PollTestHelpers.BuildGuestUseCases(voteScope)
                .VoteAsGuestAsync(pollId, linkId, optionId, VoteChoice.Available, default);
            Assert.True(voteResult.Succeeded, voteResult.Code);
        }

        await using (AsyncServiceScope removeScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult removeResult = await PollTestHelpers.BuildGuestManagementUseCases(removeScope)
                .RemoveGuestAsync(pollId, organizerId, guestIdentityId, default);
            Assert.True(removeResult.Succeeded, removeResult.Code);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        PollParticipant participant = await verifyContext.Set<PollParticipant>()
            .AsNoTracking().SingleAsync(p => p.PollId == pollId && p.GuestIdentityId == guestIdentityId);
        Assert.Equal(PollParticipantStatus.Removed, participant.Status);

        GuestLink link = await verifyContext.Set<GuestLink>()
            .AsNoTracking().SingleAsync(l => l.Id == linkId);
        Assert.Equal(GuestLinkState.RevokedByRemoval, link.State);
    }

    /// <summary>D-150: an unclaimed link revokes cleanly; a claimed link surfaces the aggregate's Conflict instead.</summary>
    [Fact]
    public async Task RevokeGuestLink_Unclaimed_Succeeds()
    {
        (PollId pollId, UserId organizerId, PollOptionId _) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildGuestManagementUseCases(scope)
            .RevokeGuestLinkAsync(pollId, organizerId, linkId, default);

        Assert.True(result.Succeeded, result.Code);
    }

    /// <summary>D-079: guest link minting is Polling-only and pre-deadline -- a poll already Closed (deadline passed) rejects a new mint.</summary>
    [Fact]
    public async Task MintGuestLinks_AfterDeadline_Rejected_D079()
    {
        (PollId pollId, UserId organizerId, PollOptionId _) = await SeedPollAsync(PollDeadlineChoice.Hours6);
        _factory.Clock.Advance(Duration.FromHours(7));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollGuestLinksResult result = await PollTestHelpers.BuildGuestManagementUseCases(scope)
            .CreateGuestLinksAsync(pollId, organizerId, 1, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_voting_closed", result.Code);
    }

    private async Task<GuestLinkId> MintGuestLinkAsync(PollId pollId, UserId organizerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollGuestLinksResult result = await PollTestHelpers.BuildGuestManagementUseCases(scope)
            .CreateGuestLinksAsync(pollId, organizerId, 1, default);
        Assert.True(result.Succeeded, result.Code);
        return result.GuestLinks![0].GuestLinkId;
    }

    private async Task<(PollId PollId, UserId OrganizerId, PollOptionId OptionId)> SeedPollAsync(
        PollDeadlineChoice deadlineChoice = PollDeadlineChoice.Hours24)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-guest-organizer-{Guid.NewGuid():N}@example.test");

        Instant now = _factory.Clock.GetCurrentInstant();
        Instant startTime = now + Duration.FromHours(2);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Guest test poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                IsPrivate: true,
                OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                DeadlineChoice: deadlineChoice,
                InviteeUserIds: [],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId, created.Poll!.Options[0].Id);
    }
}
