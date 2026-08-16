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
/// Organizer cancellation (PHASE6_DESIGN.md §2 "Cancel", ticket-P6-S3a §1): links
/// <c>RevokedByCancel</c>, pending requests <c>Lapsed</c>, D-166 notification -- all inside the one
/// A-016 transaction <see cref="CancelPollUseCase"/> wraps around <see cref="Poll.Cancel"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollCancelTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Cancel_ByOrganizer_RevokesLinksLapsesRequestsAndNotifies_D166()
    {
        (PollId pollId, UserId organizerId, UserId inviteeId) = await SeedPollAsync();

        GuestLinkId guestLinkId;
        await using (AsyncServiceScope guestScope = _factory.Services.CreateAsyncScope())
        {
            CreatePollGuestLinksResult minted = await PollTestHelpers.BuildGuestManagementUseCases(guestScope)
                .CreateGuestLinksAsync(pollId, organizerId, count: 1, default);
            Assert.True(minted.Succeeded, minted.Code);
            guestLinkId = minted.GuestLinks![0].GuestLinkId;
        }

        UserId requesterId = await _factory.SeedUserAsync(email: $"poll-cancel-requester-{Guid.NewGuid():N}@example.test");
        JoinRequestId joinRequestId;
        await using (AsyncServiceScope requestScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = requestScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, requesterId, _factory.Clock);
            await context.SaveChangesAsync();

            PollJoinRequestCommandResult requested = await PollTestHelpers.BuildJoinRequestUseCases(requestScope)
                .RequestJoinAsync(pollId, requesterId, default);
            Assert.True(requested.Succeeded, requested.Code);
            joinRequestId = requested.JoinRequest!.Id;
        }

        await using AsyncServiceScope cancelScope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildCancelUseCase(cancelScope)
            .CancelAsync(pollId, organizerId, default);
        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(PollStatus.Canceled, result.Poll!.Status);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        GuestLink link = await readContext.GuestLinks.AsNoTracking().SingleAsync(l => l.Id == guestLinkId);
        Assert.Equal(GuestLinkState.RevokedByCancel, link.State);

        JoinRequest request = await readContext.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == joinRequestId);
        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);

        Notification cancelNotice = await readContext.Notifications.AsNoTracking()
            .SingleAsync(n => n.PollId == pollId && n.Type == NotificationType.PollCanceled);
        Assert.Equal(inviteeId, cancelNotice.RecipientUserId);
    }

    /// <summary>Non-organizer cancellation attempts fail closed with the same 404 a stranger gets.</summary>
    [Fact]
    public async Task Cancel_ByNonOrganizer_FailsClosed()
    {
        (PollId pollId, _, UserId inviteeId) = await SeedPollAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildCancelUseCase(scope)
            .CancelAsync(pollId, inviteeId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_not_found", result.Code);
    }

    /// <summary>
    /// D-156 / ticket-P6-S4a-fix1 Ruling A: a poll past the 48-hour selection window has already
    /// expired. <c>CancelPollUseCase</c> now surfaces this as the design-named
    /// <c>poll_selection_window_closed</c> (not the generic <c>poll_invalid_transition</c> the
    /// aggregate's own due-transition-then-status-guard path would have produced), but still applies
    /// and materializes the due Expire transition itself (§1's materialized-status invariant), so the
    /// reload assertion below is unchanged.
    /// </summary>
    [Fact]
    public async Task Cancel_AfterSelectionWindowEnds_SurfacesSelectionWindowClosed_D156_RulingA()
    {
        (PollId pollId, UserId organizerId, _) = await SeedPollAsync(PollDeadlineChoice.Hours6);

        _factory.Clock.Advance(Duration.FromHours(6) + Duration.FromHours(48) + Duration.FromMinutes(1));

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        PollCommandResult result = await PollTestHelpers.BuildCancelUseCase(scope)
            .CancelAsync(pollId, organizerId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("poll_selection_window_closed", result.Code);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Poll reloaded = await readContext.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        Assert.Equal(PollStatus.Expired, reloaded.Status);
    }

    /// <summary>Cancellation is idempotent: a second cancel is a silent no-op, not an error.</summary>
    [Fact]
    public async Task Cancel_ASecondTime_IsANoOp()
    {
        (PollId pollId, UserId organizerId, _) = await SeedPollAsync();

        await using (AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope())
        {
            PollCommandResult first = await PollTestHelpers.BuildCancelUseCase(firstScope)
                .CancelAsync(pollId, organizerId, default);
            Assert.True(first.Succeeded, first.Code);
        }

        await using AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope();
        PollCommandResult second = await PollTestHelpers.BuildCancelUseCase(secondScope)
            .CancelAsync(pollId, organizerId, default);

        Assert.True(second.Succeeded, second.Code);
        Assert.Equal(PollStatus.Canceled, second.Poll!.Status);
    }

    /// <summary>
    /// A-016 against the real transactional store (see <c>PollCommandTransactionTests</c> for why the
    /// in-memory unit-test fakes cannot prove a mutation's rollback): a thrown materialization failure
    /// rolls Postgres's own transaction back, so the poll row is still <c>Polling</c> and no
    /// notification row exists.
    /// </summary>
    [Fact]
    public async Task Cancel_RollsBackWhenNotificationMaterializationFails_A016()
    {
        (PollId pollId, UserId organizerId, _) = await SeedPollAsync();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var useCase = new CancelPollUseCase(
            scope.ServiceProvider.GetRequiredService<IPollRepository>(),
            scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
            new ThrowingPollNotificationMaterializer(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IClock>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.CancelAsync(pollId, organizerId, default));

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Poll reloaded = await readContext.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        Assert.Equal(PollStatus.Polling, reloaded.Status);

        // Creation's own PollInvitationSent notification (a prior, already-committed transaction)
        // legitimately has this PollId -- the rollback proof is that no PollCanceled row joined it.
        Assert.False(await readContext.Notifications.AsNoTracking()
            .AnyAsync(n => n.PollId == pollId && n.Type == NotificationType.PollCanceled));
    }

    private async Task<(PollId PollId, UserId OrganizerId, UserId InviteeId)> SeedPollAsync(
        PollDeadlineChoice deadlineChoice = PollDeadlineChoice.Hours24)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-cancel-organizer-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(email: $"poll-cancel-invitee-{Guid.NewGuid():N}@example.test");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, inviteeId, _factory.Clock);
        await context.SaveChangesAsync();

        Instant now = _factory.Clock.GetCurrentInstant();
        CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(scope).ExecuteAsync(
            new CreatePollCommand(
                organizerId,
                "Cancel test poll",
                DurationMinutes: 60,
                Platform: null,
                Game: null,
                Notes: null,
                MinAttendance: 1,
                MaxCapacity: null,
                Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                OptionStartTimes: [now + Duration.FromHours(2), now + Duration.FromHours(3)],
                DeadlineChoice: deadlineChoice,
                InviteeUserIds: [inviteeId],
                GuestLinkCount: null),
            default);
        Assert.True(created.Succeeded, created.Code);

        return (created.Poll!.Id, organizerId, inviteeId);
    }
}

/// <summary>Shared A-016 rollback fixture: a real-store failure point for both Cancel and Finalize.</summary>
internal sealed class ThrowingPollNotificationMaterializer : IPollNotificationMaterializer
{
    public Task MaterializeAsync(Poll poll, IReadOnlyList<IDomainEvent> events, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Simulated notification-write failure.");
}
