using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
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

    /// <summary>D-017: voting before claiming is a 409-shaped application error -- claiming is explicit.</summary>
    [Fact]
    public async Task Guest_VoteBeforeClaim_ReturnsNotClaimed409_D017()
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

    /// <summary>
    /// D-126 multi-device proof: two contexts race a rename of the
    /// same claimed link while genuinely blocked on the same held poll parent lock, and only the write
    /// that actually acquires the lock second survives -- no version/conflict rejection either way.
    /// Run twice with the queuing order swapped, so the surviving value flips with it: a build that
    /// silently favored (or rejected) whichever rename call happened to be issued first in code --
    /// rather than genuinely serializing on the parent lock -- would produce the same winner both
    /// times, or a conflict error, and this test would catch either.
    /// </summary>
    [Fact]
    public async Task Guest_Rename_ConcurrentRace_LastWriteWins_D126()
    {
        (PollId pollId, UserId organizerId, PollOptionId _) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult claimResult = await PollTestHelpers.BuildGuestUseCases(claimScope)
                .ClaimGuestLinkAsync(pollId, linkId, DisplayName.Create("Initial Name"), default);
            Assert.True(claimResult.Succeeded, claimResult.Code);
        }

        string firstOrderWinner = await RaceRenameAsync(pollId, linkId, "Ordering One - Queued First", "Ordering One - Queued Second");
        Assert.Equal("Ordering One - Queued Second", firstOrderWinner);

        string secondOrderWinner = await RaceRenameAsync(pollId, linkId, "Ordering Two - Queued Second", "Ordering Two - Queued First");
        Assert.Equal("Ordering Two - Queued First", secondOrderWinner);
    }

    /// <summary>
    /// Holds the poll parent lock in one transaction, queues two <see cref="GuestPollUseCases.RenameGuestAsync"/>
    /// calls against the same link while both are provably still blocked on it (mirrors
    /// <see cref="PollLockingTests.VoteCommandBlocksOnAnExternallyHeldPollParentLock"/>'s blocking proof),
    /// then releases the lock and returns whichever value the row ends up holding.
    /// </summary>
    private async Task<string> RaceRenameAsync(
        PollId pollId,
        GuestLinkId linkId,
        string queuedFirstValue,
        string queuedSecondValue)
    {
        await using AsyncServiceScope holderScope = _factory.Services.CreateAsyncScope();
        AppDbContext holderContext = holderScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var holderTransaction = await holderContext.Database.BeginTransactionAsync();
        await holderContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM polls WHERE id = {pollId.Value} FOR NO KEY UPDATE");

        await using AsyncServiceScope scope1 = _factory.Services.CreateAsyncScope();
        Task<GuestPollCommandResult> task1 = PollTestHelpers.BuildGuestUseCases(scope1)
            .RenameGuestAsync(pollId, linkId, DisplayName.Create(queuedFirstValue), default);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(task1.IsCompleted, "The first rename must still be blocked on the externally held poll parent lock.");

        await using AsyncServiceScope scope2 = _factory.Services.CreateAsyncScope();
        Task<GuestPollCommandResult> task2 = PollTestHelpers.BuildGuestUseCases(scope2)
            .RenameGuestAsync(pollId, linkId, DisplayName.Create(queuedSecondValue), default);

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Assert.False(task2.IsCompleted, "The second rename must still be blocked on the same externally held poll parent lock.");

        await holderTransaction.RollbackAsync();

        GuestPollCommandResult result1 = await task1;
        GuestPollCommandResult result2 = await task2;
        Assert.True(result1.Succeeded, result1.Code);
        Assert.True(result2.Succeeded, result2.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        GuestIdentity identity = await verifyContext.Set<GuestIdentity>()
            .AsNoTracking().SingleAsync(g => g.PollId == pollId);
        return identity.DisplayName.Value;
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

    /// <summary>
    /// D-044/D-150: a claimed link cannot be revoked directly -- the aggregate's own Conflict guard
    /// stops it, the link stays Active, and RemoveGuest remains the only path that detaches a claimed
    /// guest (proven by <see cref="RemoveGuest_RemovesVoterAndRevokesLink_OneTransaction_D044"/>).
    /// </summary>
    [Fact]
    public async Task RevokeGuestLink_Claimed_SurfacesConflictAndLinkStaysActive_D044()
    {
        (PollId pollId, UserId organizerId, PollOptionId _) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult claimResult = await PollTestHelpers.BuildGuestUseCases(claimScope)
                .ClaimGuestLinkAsync(pollId, linkId, DisplayName.Create("Claimed Before Revoke"), default);
            Assert.True(claimResult.Succeeded, claimResult.Code);
        }

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildGuestManagementUseCases(scope)
            .RevokeGuestLinkAsync(pollId, organizerId, linkId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_conflict", result.Code);

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        GuestLink link = await verifyContext.Set<GuestLink>().AsNoTracking().SingleAsync(l => l.Id == linkId);
        Assert.Equal(GuestLinkState.Active, link.State);
    }

    /// <summary>
    /// D-033: any Active voter suggests, guests included -- the guest's suggestion lands as a real
    /// option (resolved to their claimed identity, not a raw link id) and the WIRED materializer routes
    /// the §10 notification to the organizer only, since a guest is never a recipient itself (D-167).
    /// </summary>
    [Fact]
    public async Task SuggestOptionAsGuest_LandsAsOptionWithOrganizerOnlyNotification_D033()
    {
        (PollId pollId, UserId organizerId, PollOptionId _) = await SeedPollAsync();
        GuestLinkId linkId = await MintGuestLinkAsync(pollId, organizerId);

        GuestIdentityId identityId;
        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult claimResult = await PollTestHelpers.BuildGuestUseCases(claimScope)
                .ClaimGuestLinkAsync(pollId, linkId, DisplayName.Create("Suggesting Guest"), default);
            Assert.True(claimResult.Succeeded, claimResult.Code);
            identityId = claimResult.GuestIdentityId!.Value;
        }

        Instant now = _factory.Clock.GetCurrentInstant();
        Instant suggestedTime = now + Duration.FromHours(9);

        await using (AsyncServiceScope suggestScope = _factory.Services.CreateAsyncScope())
        {
            GuestPollCommandResult suggestResult = await PollTestHelpers.BuildGuestUseCases(suggestScope)
                .SuggestOptionAsGuestAsync(pollId, linkId, suggestedTime, default);
            Assert.True(suggestResult.Succeeded, suggestResult.Code);
            Assert.Contains(
                suggestResult.Poll!.Options,
                o => o.StartTime == suggestedTime && o.SuggestedByGuestIdentityId == identityId);
        }

        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        AppDbContext verifyContext = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Notification notification = await verifyContext.Notifications
            .SingleAsync(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSuggested);
        Assert.Equal(organizerId, notification.RecipientUserId);
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
                Visibility: ContentVisibility.Private,
            AudienceGroupIds: null,
                OptionStartTimes: [startTime, startTime + Duration.FromHours(1)],
                DeadlineChoice: deadlineChoice,
                InviteeUserIds: [],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId, created.Poll!.Options[0].Id);
    }
}
