using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// Organizer finalization (PHASE6_DESIGN.md §2 "Finalize", ticket-P6-S3a §2): the application-layer
/// wrapper around <see cref="Poll.Finalize"/> -- the §6.4 branch matrix and allocation order are
/// proven at the domain level (<c>PollFinalizationTests</c>, TRACEABILITY "Domain | 1"); these tests
/// prove the transaction's own responsibilities: the session persists, its guest links/identities and
/// mutes re-parent at the DB level (§5.4/§12-15), the option-selected/waitlist notifications
/// materialize, and reminders are replanned only where the session is still future (§9).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollFinalizationCommandTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Finalize_HappyPath_PersistsSessionAndNotifiesEveryVoter_D153()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4));

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult voted = await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Available, default);
            Assert.True(voted.Succeeded, voted.Code);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(PollStatus.Finalized, result.Poll!.Status);
        Assert.Equal(SessionStatus.Confirmed, result.Session!.Status);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Session persisted = await readContext.Sessions.AsNoTracking()
            .Include(s => s.Participations)
            .SingleAsync(s => s.Id == result.Session!.Id);
        Assert.Equal(pollId, persisted.OriginPollId);
        Assert.Equal(2, persisted.Participations.Count);

        List<Notification> optionSelected = await readContext.Notifications.AsNoTracking()
            .Where(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSelected)
            .ToListAsync();
        Assert.Equal(2, optionSelected.Count);
        Assert.All(optionSelected, n => Assert.Equal(result.Session!.Id, n.SessionId));
        Assert.Contains(optionSelected, n => n.RecipientUserId == organizerId);
        Assert.Contains(optionSelected, n => n.RecipientUserId == inviteeId);
    }

    /// <summary>D-119: the overflow is waitlisted and receives its own explicit-target notification.</summary>
    [Fact]
    public async Task Finalize_WaitlistOverflow_NotifiesTheWaitlistedParticipant_D119()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-fin-wl-org-{Guid.NewGuid():N}@example.test");
        UserId inviteeA = await _factory.SeedUserAsync(email: $"poll-fin-wl-a-{Guid.NewGuid():N}@example.test");
        UserId inviteeB = await _factory.SeedUserAsync(email: $"poll-fin-wl-b-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeA, _factory.Clock);
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeB, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Waitlist overflow finalize poll",
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: 2,
                Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                Options: [new CreatePollOptionInput(now + Duration.FromDays(2), 60), new CreatePollOptionInput(now + Duration.FromDays(3), 60)],
                DeadlineChoice: PollDeadlineChoice.Hours6,
                InviteeUserIds: [inviteeA, inviteeB],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);
        PollId pollId = created.Poll!.Id;
        PollOptionId optionId = created.Poll!.Options[0].Id;

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeA, optionId, VoteChoice.Available, default)).Succeeded);
        }

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeB, optionId, VoteChoice.Available, default)).Succeeded);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Notification waitlisted = await readContext.Notifications.AsNoTracking()
            .SingleAsync(n => n.PollId == pollId && n.Type == NotificationType.WaitlistAdded);
        Assert.Equal(inviteeB, waitlisted.RecipientUserId);
        Assert.Equal(result.Session!.Id, waitlisted.SessionId);
    }

    /// <summary>§12-15: the poll's mute re-parents onto the session, not deleted and recreated.</summary>
    [Fact]
    public async Task Finalize_CarriesTheMuteOntoTheSessionAtTheDbLevel_D090()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4));

        NotificationMuteId muteId;
        await using (AsyncServiceScope muteScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = muteScope.ServiceProvider.GetRequiredService<AppDbContext>();
            NotificationMute mute = NotificationMute.ForPoll(inviteeId, pollId, _factory.Clock);
            context.NotificationMutes.Add(mute);
            await context.SaveChangesAsync();
            muteId = mute.Id;
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        NotificationMute reloaded = await readContext.NotificationMutes.AsNoTracking().SingleAsync(m => m.Id == muteId);
        Assert.Null(reloaded.PollId);
        Assert.Equal(result.Session!.Id, reloaded.SessionId);

        // §9/D-090: the mute must still suppress the PollClosed the same Finalize call raises via
        // ApplyDueTransitions -- proving the write-time mute lookup sees the pre-re-parent PollId.
        List<Notification> pollClosed = await readContext.Notifications.AsNoTracking()
            .Where(n => n.PollId == pollId && n.Type == NotificationType.PollClosed)
            .ToListAsync();
        Assert.DoesNotContain(pollClosed, n => n.RecipientUserId == inviteeId);
        Assert.Contains(pollClosed, n => n.RecipientUserId == organizerId);
    }

    /// <summary>§5.4/A-015: the re-parented guest link keeps its stored token digest byte-for-byte.</summary>
    [Fact]
    public async Task Finalize_ReParentsTheGuestLinkWithTheTokenHashUnchanged_D048()
    {
        (PollId pollId, UserId organizerId, _, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4));

        GuestLinkId guestLinkId;
        string originalTokenHash;
        await using (AsyncServiceScope guestScope = _factory.Services.CreateAsyncScope())
        {
            CreatePollGuestLinksResult minted = await PollTestHelpers.BuildGuestManagementUseCases(guestScope)
                .CreateGuestLinksAsync(pollId, organizerId, count: 1, default);
            Assert.True(minted.Succeeded, minted.Code);
            guestLinkId = minted.GuestLinks![0].GuestLinkId;

            GuestPollCommandResult claimed = await PollTestHelpers.BuildGuestUseCases(guestScope)
                .ClaimGuestLinkAsync(pollId, guestLinkId, DisplayName.Create("Finalize Guest"), default);
            Assert.True(claimed.Succeeded, claimed.Code);

            GuestPollCommandResult voted = await PollTestHelpers.BuildGuestUseCases(guestScope)
                .VoteAsGuestAsync(pollId, guestLinkId, optionId, VoteChoice.Available, default);
            Assert.True(voted.Succeeded, voted.Code);
        }

        await using (AsyncServiceScope probeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = probeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            GuestLink beforeFinalize = await context.GuestLinks.AsNoTracking().SingleAsync(l => l.Id == guestLinkId);
            originalTokenHash = beforeFinalize.TokenHash;
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        GuestLink reloaded = await readContext.GuestLinks.AsNoTracking().SingleAsync(l => l.Id == guestLinkId);
        Assert.Null(reloaded.PollId);
        Assert.Equal(result.Session!.Id, reloaded.SessionId);
        Assert.Equal(originalTokenHash, reloaded.TokenHash);

        GuestIdentity identity = await readContext.GuestIdentities.AsNoTracking()
            .SingleAsync(g => g.Id == reloaded.GuestIdentityId);
        Assert.Null(identity.PollId);
        Assert.Equal(result.Session!.Id, identity.SessionId);
    }

    /// <summary>§9: a future session's Going participant gets a durable reminder handoff planned.</summary>
    [Fact]
    public async Task Finalize_PlansAReminderHandoffForAFutureSession_D082()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4));

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Available, default)).Succeeded);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(SessionStatus.Confirmed, result.Session!.Status);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        int handoffCount = await readContext.SessionReminderHandoffs.AsNoTracking()
            .Where(h => h.SessionId == result.Session!.Id)
            .CountAsync();
        Assert.Equal(2, handoffCount);
    }

    /// <summary>§9/D-153: a completed (already-ended) session is terminal, so no reminder handoff is ever written.</summary>
    [Fact]
    public async Task Finalize_WritesNoReminderHandoffForAnAlreadyEndedSession_D153()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-fin-past-org-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"poll-fin-past-invitee-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Already-ended finalize poll",
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                Options: [new CreatePollOptionInput(now - Duration.FromHours(10), 60), new CreatePollOptionInput(now + Duration.FromDays(3), 60)],
                DeadlineChoice: PollDeadlineChoice.Hours6,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);
        PollId pollId = created.Poll!.Id;
        PollOptionId endedOptionId = created.Poll!.Options[0].Id;

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, endedOptionId, VoteChoice.Available, default)).Succeeded);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, endedOptionId, default);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(SessionStatus.Completed, result.Session!.Status);
        Assert.True(result.Session!.IsRetainedInHistory);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        int handoffCount = await readContext.SessionReminderHandoffs.AsNoTracking()
            .Where(h => h.SessionId == result.Session!.Id)
            .CountAsync();
        Assert.Equal(0, handoffCount);

        int optionSelectedCount = await readContext.Notifications.AsNoTracking()
            .Where(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSelected)
            .CountAsync();
        Assert.Equal(2, optionSelectedCount);
    }

    /// <summary>§6.4/D-153: a started-but-not-ended option (now ∈ [start, end)) settles as Confirmed
    /// with the session already in progress by clock evaluation, not settled Completed/Expired.</summary>
    [Fact]
    public async Task Finalize_SelectsAStartedOptionAsInProgress_D153()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromHours(5) + Duration.FromMinutes(50));

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Available, default)).Succeeded);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(SessionStatus.Confirmed, result.Session!.Status);
        Assert.True(result.Session!.IsInProgress(_factory.Clock));

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        int optionSelectedCount = await readContext.Notifications.AsNoTracking()
            .Where(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSelected)
            .CountAsync();
        Assert.Equal(2, optionSelectedCount);
    }

    [Fact]
    public async Task Finalize_ByNonOrganizer_FailsClosed()
    {
        (PollId pollId, _, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(2));

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(scope)
            .FinalizeAsync(pollId, inviteeId, optionId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_not_found", result.Code);
    }

    /// <summary>
    /// A-016 against the real transactional store: a thrown materialization failure rolls the poll's
    /// <c>Finalized</c> transition and the minted session back together (see
    /// <c>PollCommandTransactionTests</c> for why this cannot be proven with the in-memory unit-test
    /// fakes).
    /// </summary>
    [Fact]
    public async Task Finalize_RollsBackWhenNotificationMaterializationFails_A016()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4));

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True((await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Available, default)).Succeeded);
        }

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromMinutes(1));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var useCase = new FinalizePollUseCase(
            scope.ServiceProvider.GetRequiredService<IPollRepository>(),
            scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
            scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
            new ThrowingPollNotificationMaterializer(),
            scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.FinalizeAsync(pollId, organizerId, optionId, default));

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Poll reloaded = await readContext.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        Assert.Equal(PollStatus.Polling, reloaded.Status);
        Assert.False(await readContext.Sessions.AsNoTracking().AnyAsync(s => s.OriginPollId == pollId));

        // Creation's own PollInvitationSent notification (a prior, already-committed transaction)
        // legitimately has this PollId -- the rollback proof is that no PollOptionSelected row joined it.
        Assert.False(await readContext.Notifications.AsNoTracking()
            .AnyAsync(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSelected));
    }

    /// <summary>
    /// D-187 over the real transaction: before the deadline the organizer's selection is refused
    /// while the option is short of the minimum and committed once it is not -- one path, so the
    /// early finalization persists the same session, notifications and status any other would.
    /// </summary>
    [Fact]
    public async Task Finalize_BeforeTheDeadline_IsRefusedUntilTheOptionIsViable_D187()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4), minAttendance: 2);

        await using (AsyncServiceScope earlyScope = _factory.Services.CreateAsyncScope())
        {
            FinalizePollResult tooEarly = await PollTestHelpers.BuildFinalizeUseCase(earlyScope)
                .FinalizeAsync(pollId, organizerId, optionId, default);
            Assert.False(tooEarly.Succeeded);
            Assert.Equal("poll_invalid_transition", tooEarly.Code);
        }

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult voted = await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Available, default);
            Assert.True(voted.Succeeded, voted.Code);
        }

        // Still inside the 6-hour voting window: the organizer, plus one Available vote, is the
        // minimum -- the poll goes Polling -> Finalized without ever materializing Closed.
        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(PollStatus.Finalized, result.Poll!.Status);
        Assert.Equal(SessionStatus.Confirmed, result.Session!.Status);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Poll persistedPoll = await readContext.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        Assert.Equal(PollStatus.Finalized, persistedPoll.Status);
        Assert.Equal(optionId, persistedPoll.SelectedOptionId);

        Session persisted = await readContext.Sessions.AsNoTracking()
            .Include(s => s.Participations)
            .SingleAsync(s => s.Id == result.Session!.Id);
        Assert.Equal(2, persisted.Participations.Count);

        List<Notification> optionSelected = await readContext.Notifications.AsNoTracking()
            .Where(n => n.PollId == pollId && n.Type == NotificationType.PollOptionSelected)
            .ToListAsync();
        Assert.Equal(2, optionSelected.Count);
    }

    /// <summary>D-187 counts Available only: a Maybe maps to Maybe (D-051) and never opens the gate.</summary>
    [Fact]
    public async Task Finalize_BeforeTheDeadline_DoesNotCountAMaybeVote_D187()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId, PollOptionId optionId) =
            await SeedPollAsync(startOffset: Duration.FromDays(4), minAttendance: 2);

        await using (AsyncServiceScope voteScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult voted = await PollTestHelpers.BuildVoteUseCases(voteScope)
                .VoteAsync(pollId, inviteeId, optionId, VoteChoice.Maybe, default);
            Assert.True(voted.Succeeded, voted.Code);
        }

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_invalid_transition", result.Code);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            PollStatus.Polling,
            (await readContext.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId)).Status);
        Assert.False(await readContext.Sessions.AsNoTracking().AnyAsync(s => s.OriginPollId == pollId));
    }

    private async Task<(PollId PollId, UserId OrganizerId, UserId InviteeId, PollOptionId OptionId)> SeedPollAsync(
        Duration startOffset,
        int minAttendance = 1)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-fin-org-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"poll-fin-invitee-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Finalize command test poll",
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: minAttendance,
                MaxCapacity: null,
                Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                Options: [new CreatePollOptionInput(now + startOffset, 60), new CreatePollOptionInput(now + startOffset + Duration.FromHours(1), 60)],
                DeadlineChoice: PollDeadlineChoice.Hours6,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId, inviteeId, created.Poll!.Options[0].Id);
    }
}
