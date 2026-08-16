using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Polls;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Domain.Visibility;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Friends;

[Trait("Category", "Integration")]
public sealed class DeletionSweepTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task BeforeExpiry_ChangesNoFriendGraphRow_D148()
    {
        Phase3DeletionSeed seed = await Phase3DeletionTestSupport.SeedAsync(_factory);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ExecuteDeletionForUserUseCase useCase = Phase3DeletionTestSupport.CreateUseCase(
            scope.ServiceProvider);

        Assert.False(await useCase.ExecuteAsync(seed.SubjectId, default));

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        User subject = await context.DomainUsers.AsNoTracking()
            .SingleAsync(user => user.Id == seed.SubjectId);
        Assert.True(subject.IsPendingDeletion);
        Assert.Equal(3, await context.Friendships.CountAsync());
        Assert.Equal(3, await context.Blocks.CountAsync());
        Assert.Equal(3, await context.Notifications.CountAsync());
        Assert.Equal(UsernameRegistryState.Active, (await context.UsernameRegistry.AsNoTracking()
            .SingleAsync(entry => entry.OwnerId == seed.SubjectId)).State);
        Assert.DoesNotContain(
            (await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default)),
            entry => entry.UserId == seed.SubjectId);
    }

    [Fact]
    public async Task ExpiryDeletesRecipientNotificationsAndPreservesActorOnlyRowsWithTombstoneMask_D092()
    {
        Phase3DeletionSeed seed = await Phase3DeletionTestSupport.SeedAsync(_factory);
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Assert.True(await Phase3DeletionTestSupport.CreateUseCase(scope.ServiceProvider)
                .ExecuteAsync(seed.SubjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        User subject = await context.DomainUsers.AsNoTracking()
            .SingleAsync(user => user.Id == seed.SubjectId);
        Assert.True(subject.IsTombstoned);
        Assert.Equal(0, await context.Friendships.CountAsync(friendship =>
            friendship.UserLoId == seed.SubjectId || friendship.UserHiId == seed.SubjectId));
        Assert.Equal(0, await context.Blocks.CountAsync(block =>
            block.BlockerId == seed.SubjectId || block.BlockedId == seed.SubjectId));
        Assert.Equal(0, await context.Notifications.CountAsync(notification =>
            notification.RecipientUserId == seed.SubjectId));
        Notification actorOnly = await context.Notifications.AsNoTracking()
            .SingleAsync(notification => notification.Id == seed.ActorOnlyNotificationId);
        Assert.Equal(seed.SubjectId, actorOnly.ActorUserId);
        Assert.True(await context.Notifications.AnyAsync(notification =>
            notification.Id == seed.ControlNotificationId));
        Assert.True(await context.Friendships.AnyAsync(friendship =>
            friendship.Id == seed.ControlFriendshipId));
        Assert.True(await context.Blocks.AnyAsync(block => block.Id == seed.ControlBlockId));
        UsernameRegistryEntry username = await context.UsernameRegistry.AsNoTracking()
            .SingleAsync(entry => entry.NameCanonical == seed.SubjectCanonicalUsername);
        Assert.True(username.IsReserved);
        Assert.Null(username.OwnerId);
        MaskedIdentity rendered = IdentityMasking.ForUser(
            subject,
            Viewer.Create(seed.FirstCounterpartId));
        Assert.True(rendered.IsAnonymous);
        Assert.Equal(MaskingReason.Tombstoned, rendered.Reason);
        Assert.Contains(
            (await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default)),
            entry => entry.UserId == seed.SubjectId);
        Assert.Null(await verification.ServiceProvider.GetRequiredService<IUserAccountGateway>()
            .GetEmailAsync(seed.SubjectId, default));
    }

    [Fact]
    public async Task FailureAfterResultApplier_RollsBackEveryPhase3RowAndJournal_D092()
    {
        Phase3DeletionSeed seed = await Phase3DeletionTestSupport.SeedAsync(
            _factory,
            password: "Rollback-Credentials-42!");
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            IUserAccountGateway failing = new PartyUp.Api.IntegrationTests.FailingPurgeAccountGateway(
                scope.ServiceProvider.GetRequiredService<IUserAccountGateway>());
            ExecuteDeletionForUserUseCase useCase = Phase3DeletionTestSupport.CreateUseCase(
                scope.ServiceProvider,
                accounts: failing);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                useCase.ExecuteAsync(seed.SubjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        User subject = await context.DomainUsers.AsNoTracking()
            .SingleAsync(user => user.Id == seed.SubjectId);
        Assert.True(subject.IsPendingDeletion);
        Assert.Equal(seed.SubjectUsername, subject.Username!.Value.Value);
        Assert.Equal(seed.SubjectDisplayName, subject.DisplayName!.Value.Value);
        Assert.Equal(seed.SubjectFriendCode, subject.FriendCode!.Value.Value);
        Assert.Equal(seed.GraceEndsAt, subject.GraceEndsAt);
        UsernameRegistryEntry username = await context.UsernameRegistry.AsNoTracking()
            .SingleAsync(entry => entry.NameCanonical == seed.SubjectCanonicalUsername);
        Assert.Equal(UsernameRegistryState.Active, username.State);
        Assert.Equal(seed.SubjectId, username.OwnerId);
        Assert.True(await context.Friendships.AnyAsync(friendship =>
            friendship.Id == seed.PendingFriendshipId && friendship.Status == FriendshipStatus.Pending));
        Assert.True(await context.Friendships.AnyAsync(friendship =>
            friendship.Id == seed.AcceptedFriendshipId && friendship.Status == FriendshipStatus.Accepted));
        Assert.True(await context.Friendships.AnyAsync(friendship =>
            friendship.Id == seed.ControlFriendshipId));
        Assert.True(await context.Blocks.AnyAsync(block => block.Id == seed.OutboundBlockId));
        Assert.True(await context.Blocks.AnyAsync(block => block.Id == seed.InboundBlockId));
        Assert.True(await context.Blocks.AnyAsync(block => block.Id == seed.ControlBlockId));
        Assert.True(await context.Notifications.AnyAsync(notification =>
            notification.Id == seed.RecipientNotificationId));
        Assert.True(await context.Notifications.AnyAsync(notification =>
            notification.Id == seed.ActorOnlyNotificationId));
        Assert.True(await context.Notifications.AnyAsync(notification =>
            notification.Id == seed.ControlNotificationId));
        IUserAccountGateway accounts = verification.ServiceProvider
            .GetRequiredService<IUserAccountGateway>();
        Assert.Equal(seed.SubjectEmail, (await accounts.GetEmailAsync(seed.SubjectId, default))!.Email);
        Assert.True(await accounts.HasPasswordAsync(seed.SubjectId, default));
        Assert.DoesNotContain(
            (await verification.ServiceProvider.GetRequiredService<IDeletionJournal>()
                .ReadSinceAsync(Instant.MinValue, 100, default)),
            entry => entry.UserId == seed.SubjectId);
    }

    /// <summary>
    /// PHASE6_DESIGN.md §2.1 / ticket-P6-S3.md §5: the DeletionScopeProvider poll half feeds
    /// <c>DeletionExecutor.Execute</c>'s shipped step 2 (§11, <c>ExpireForOrganizerDeletion</c>,
    /// D-170) real owned-poll scope; a second run over the already-tombstoned subject repeats
    /// nothing (D-092) -- the poll analogue of the shipped session run-twice proof. The drained
    /// <c>PollExpired</c> event now reaches <see cref="IPollNotificationMaterializer"/> through
    /// <c>ExecuteDeletionForUserUseCase</c> (mirroring the shipped session-event materialization
    /// path exactly): the registered invitee gets exactly one <c>PollExpired</c> row, the deleting
    /// owner gets none (§10/D-170's own-deletion exclusion), and the second, no-op run emits no
    /// second row.
    /// </summary>
    [Fact]
    public async Task Execute_ExpiresOwnedPollsAndRunsTwiceWithTheSameEndState_D092()
    {
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: $"delete-poll-owner-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(
            email: $"delete-poll-invitee-{Guid.NewGuid():N}@example.test");

        PollId pollId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, subjectId, inviteeId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    subjectId,
                    "Deletion-owned poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromDays(2), now + Duration.FromDays(3)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [inviteeId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
        }

        _factory.Clock.Advance(User.DeletionGracePeriod);

        await using (AsyncServiceScope firstRun = _factory.Services.CreateAsyncScope())
        {
            Assert.True(await Phase3DeletionTestSupport.CreateUseCase(firstRun.ServiceProvider)
                .ExecuteAsync(subjectId, default));
        }

        await using (AsyncServiceScope firstRead = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = firstRead.ServiceProvider.GetRequiredService<AppDbContext>();
            Poll afterFirstRun = await context.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
            Assert.Equal(PollStatus.Expired, afterFirstRun.Status);

            Notification invited = await context.Notifications.AsNoTracking().SingleAsync(n =>
                n.PollId == pollId &&
                n.Type == NotificationType.PollExpired &&
                n.RecipientUserId == inviteeId);
            Assert.Equal(pollId, invited.PollId);
            Assert.Equal(0, await context.Notifications.CountAsync(n =>
                n.PollId == pollId &&
                n.Type == NotificationType.PollExpired &&
                n.RecipientUserId == subjectId));
        }

        await using (AsyncServiceScope secondRun = _factory.Services.CreateAsyncScope())
        {
            Assert.False(await Phase3DeletionTestSupport.CreateUseCase(secondRun.ServiceProvider)
                .ExecuteAsync(subjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Poll afterSecondRun = await verify.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        Assert.Equal(PollStatus.Expired, afterSecondRun.Status);
        Assert.Equal(1, await verify.Notifications.CountAsync(n =>
            n.PollId == pollId && n.Type == NotificationType.PollExpired));
    }

    /// <summary>
    /// ticket-P6-S3-fix1 F7: resolves <see cref="ExecuteDeletionForUserUseCase"/> straight from the
    /// real DI container (not <c>Phase3DeletionTestSupport.CreateUseCase</c>'s manual per-argument
    /// <c>GetRequiredService</c> wiring) so a future deregistration of the optional
    /// <see cref="IPollNotificationMaterializer"/> parameter fails this test instead of silently
    /// resolving the use case with a null poll materializer -- the D-170 row landing proves it arrived.
    /// </summary>
    [Fact]
    public async Task Execute_ResolvedFromTheRealContainer_StillMaterializesThePollExpiredRow_D170()
    {
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: $"delete-poll-container-owner-{Guid.NewGuid():N}@example.test");
        UserId inviteeId = await _factory.SeedUserAsync(
            email: $"delete-poll-container-invitee-{Guid.NewGuid():N}@example.test");

        PollId pollId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, subjectId, inviteeId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    subjectId,
                    "Deletion container-resolved poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromDays(2), now + Duration.FromDays(3)],
                    DeadlineChoice: PollDeadlineChoice.Hours24,
                    InviteeUserIds: [inviteeId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
        }

        _factory.Clock.Advance(User.DeletionGracePeriod);

        await using (AsyncServiceScope runScope = _factory.Services.CreateAsyncScope())
        {
            var useCase = runScope.ServiceProvider.GetRequiredService<ExecuteDeletionForUserUseCase>();
            Assert.True(await useCase.ExecuteAsync(subjectId, default));
        }

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Poll afterRun = await readContext.Polls.AsNoTracking().SingleAsync(p => p.Id == pollId);
        Assert.Equal(PollStatus.Expired, afterRun.Status);

        Notification invited = await readContext.Notifications.AsNoTracking().SingleAsync(n =>
            n.PollId == pollId &&
            n.Type == NotificationType.PollExpired &&
            n.RecipientUserId == inviteeId);
        Assert.Equal(pollId, invited.PollId);
    }

    /// <summary>
    /// §11 step 3: the subject's row on somebody else's poll becomes <c>Removed</c> and their pending
    /// join request on a third poll lapses -- the poll analogues of the shipped session removal/lapse
    /// proof.
    /// </summary>
    [Fact]
    public async Task Execute_RemovesSubjectFromOthersPollParticipantAndLapsesTheirOpenJoinRequest_D092()
    {
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: $"delete-poll-subject-{Guid.NewGuid():N}@example.test");
        UserId hostId = await _factory.SeedUserAsync(
            email: $"delete-poll-host-{Guid.NewGuid():N}@example.test");
        UserId requestHostId = await _factory.SeedUserAsync(
            email: $"delete-poll-req-host-{Guid.NewGuid():N}@example.test");
        UserId requestHostFriendId = await _factory.SeedUserAsync(
            email: $"delete-poll-req-host-friend-{Guid.NewGuid():N}@example.test");

        // §11 step 3's removal is skipped once a poll's 48h selection window has logically ended
        // (D-092: a deleted user must never map into a later finalization), and D-070's longest
        // deadline (48h) plus that 48h window is only 96h -- shorter than the 7-day grace period. The
        // participated poll is therefore created only after most of the grace period has already
        // elapsed, so its window still covers the grace-elapsed instant the deletion executor runs at;
        // the requested poll's join-request lapse carries no such window gate (it runs unconditionally,
        // before the window check, in the executor's own §11 step 3 loop) so its timing does not matter.
        _factory.Clock.Advance(User.DeletionGracePeriod - Duration.FromHours(84));

        PollId participatedPollId;
        PollId requestedPollId;
        JoinRequestId requestId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, hostId, subjectId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(
                context, requestHostId, requestHostFriendId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, requestHostId, subjectId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult participated = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    hostId,
                    "Others poll, subject participates",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromDays(6), now + Duration.FromDays(7)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [subjectId],
                    GuestLinkCount: null),
                default);
            Assert.True(participated.Succeeded, participated.Code);
            participatedPollId = participated.Poll!.Id;

            CreatePollResult requested = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    requestHostId,
                    "Others poll, subject requested",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromDays(6), now + Duration.FromDays(7)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [requestHostFriendId],
                    GuestLinkCount: null),
                default);
            Assert.True(requested.Succeeded, requested.Code);
            requestedPollId = requested.Poll!.Id;

            PollJoinRequestCommandResult joined = await PollTestHelpers.BuildJoinRequestUseCases(seedScope)
                .RequestJoinAsync(requestedPollId, subjectId, default);
            Assert.True(joined.Succeeded, joined.Code);
            requestId = joined.JoinRequest!.Id;
        }

        _factory.Clock.Advance(Duration.FromHours(84));

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Assert.True(await Phase3DeletionTestSupport.CreateUseCase(scope.ServiceProvider)
                .ExecuteAsync(subjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        PollParticipant participant = await verify.PollParticipants.AsNoTracking()
            .SingleAsync(p => p.PollId == participatedPollId && p.UserId == subjectId);
        Assert.True(participant.IsRemoved);
        JoinRequest request = await verify.JoinRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(JoinRequestStatus.Lapsed, request.Status);
    }

    /// <summary>
    /// §6.3: a <c>Removed</c> participant's votes never map into a later finalization -- proven here
    /// through the full deletion-then-finalize sequence, not only <c>Poll.AllocateRoster</c>'s own
    /// unit-level skip.
    /// </summary>
    [Fact]
    public async Task Execute_RemovedVotersVotesAreExcludedFromLaterFinalizationMapping_D092()
    {
        UserId subjectId = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: $"delete-poll-voter-{Guid.NewGuid():N}@example.test");
        UserId organizerId = await _factory.SeedUserAsync(
            email: $"delete-poll-fin-org-{Guid.NewGuid():N}@example.test");
        UserId stayingInviteeId = await _factory.SeedUserAsync(
            email: $"delete-poll-fin-stay-{Guid.NewGuid():N}@example.test");

        // As in the sibling removal test: §11 step 3's removal is skipped once the poll's selection
        // window has logically ended (D-092), and D-070's longest deadline (48h) plus its 48h window
        // is only 96h -- shorter than the 7-day grace period. Creating the poll after most of the
        // grace period has already elapsed keeps its window covering the grace-elapsed instant, and
        // (D-070's 48h max deadline having already passed by then) the poll is also immediately
        // finalizable the moment deletion completes, with no further clock advance needed.
        _factory.Clock.Advance(User.DeletionGracePeriod - Duration.FromHours(84));

        PollId pollId;
        PollOptionId optionId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PollTestHelpers.SeedAcceptedFriendshipAsync(context, organizerId, subjectId, _factory.Clock);
            await PollTestHelpers.SeedAcceptedFriendshipAsync(
                context, organizerId, stayingInviteeId, _factory.Clock);
            await context.SaveChangesAsync();

            Instant now = _factory.Clock.GetCurrentInstant();
            CreatePollResult created = await PollTestHelpers.BuildCreateUseCase(seedScope).ExecuteAsync(
                new CreatePollCommand(
                    organizerId,
                    "Deletion-then-finalize poll",
                    DurationMinutes: 60,
                    Platform: null,
                    Game: null,
                    Notes: null,
                    MinAttendance: 1,
                    MaxCapacity: null,
                    Visibility: ContentVisibility.Public,
            AudienceGroupIds: null,
                    OptionStartTimes: [now + Duration.FromDays(8), now + Duration.FromDays(9)],
                    DeadlineChoice: PollDeadlineChoice.Hours48,
                    InviteeUserIds: [subjectId, stayingInviteeId],
                    GuestLinkCount: null),
                default);
            Assert.True(created.Succeeded, created.Code);
            pollId = created.Poll!.Id;
            optionId = created.Poll!.Options[0].Id;

            PollCommandResult voted = await PollTestHelpers.BuildVoteUseCases(seedScope)
                .VoteAsync(pollId, subjectId, optionId, VoteChoice.Available, default);
            Assert.True(voted.Succeeded, voted.Code);
        }

        _factory.Clock.Advance(Duration.FromHours(84));

        await using (AsyncServiceScope deleteScope = _factory.Services.CreateAsyncScope())
        {
            Assert.True(await Phase3DeletionTestSupport.CreateUseCase(deleteScope.ServiceProvider)
                .ExecuteAsync(subjectId, default));
        }

        await using (AsyncServiceScope verifyRemoved = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = verifyRemoved.ServiceProvider.GetRequiredService<AppDbContext>();
            PollParticipant removed = await context.PollParticipants.AsNoTracking()
                .SingleAsync(p => p.PollId == pollId && p.UserId == subjectId);
            Assert.True(removed.IsRemoved);
        }

        await using AsyncServiceScope finalizeScope = _factory.Services.CreateAsyncScope();
        FinalizePollResult result = await PollTestHelpers.BuildFinalizeUseCase(finalizeScope)
            .FinalizeAsync(pollId, organizerId, optionId, default);
        Assert.True(result.Succeeded, result.Code);

        await using AsyncServiceScope readScope = _factory.Services.CreateAsyncScope();
        AppDbContext readContext = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<Participation> participations = await readContext.SessionParticipations.AsNoTracking()
            .Where(p => p.SessionId == result.Session!.Id)
            .ToListAsync();
        Assert.DoesNotContain(participations, p => p.UserId == subjectId);
        Assert.Contains(participations, p => p.UserId == organizerId);
        Assert.Contains(participations, p => p.UserId == stayingInviteeId);
    }
}

internal sealed record Phase3DeletionSeed(
    UserId SubjectId,
    UserId FirstCounterpartId,
    string SubjectEmail,
    string SubjectUsername,
    string SubjectCanonicalUsername,
    string SubjectDisplayName,
    string SubjectFriendCode,
    Instant GraceEndsAt,
    FriendshipId PendingFriendshipId,
    FriendshipId AcceptedFriendshipId,
    FriendshipId ControlFriendshipId,
    BlockId OutboundBlockId,
    BlockId InboundBlockId,
    BlockId ControlBlockId,
    NotificationId RecipientNotificationId,
    NotificationId ActorOnlyNotificationId,
    NotificationId ControlNotificationId);

internal static class Phase3DeletionTestSupport
{
    internal static async Task<Phase3DeletionSeed> SeedAsync(
        PartyUpApiFactory factory,
        string? password = null)
    {
        string suffix = Guid.NewGuid().ToString("N");
        string subjectEmail = $"delete-subject-{suffix}@example.test";
        UserId subjectId = await factory.SeedUserAsync(
            pendingDeletion: true,
            email: subjectEmail,
            password: password);
        UserId firstId = await factory.SeedUserAsync(
            email: $"delete-first-{suffix}@example.test");
        UserId secondId = await factory.SeedUserAsync(
            email: $"delete-second-{suffix}@example.test");
        UserId controlId = await factory.SeedUserAsync(
            email: $"delete-control-{suffix}@example.test");
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User subject = await context.DomainUsers.SingleAsync(user => user.Id == subjectId);
        User first = await context.DomainUsers.SingleAsync(user => user.Id == firstId);
        User second = await context.DomainUsers.SingleAsync(user => user.Id == secondId);
        User control = await context.DomainUsers.SingleAsync(user => user.Id == controlId);
        UsernameRegistryEntry username = UsernameRegistryEntry.ClaimNew(
            subject.Username!.Value,
            subject.Id);
        Friendship pending = Friendship.Request(subject, first, factory.Clock);
        _ = pending.DequeueEvents();
        Friendship accepted = Friendship.Request(subject, second, factory.Clock);
        accepted.Accept(second.Id, factory.Clock);
        _ = accepted.DequeueEvents();
        Friendship controlFriendship = Friendship.Request(first, control, factory.Clock);
        controlFriendship.Accept(control.Id, factory.Clock);
        _ = controlFriendship.DequeueEvents();
        Block outbound = Block.Create(
            subject.Id,
            first.Id,
            first.Username!.Value,
            factory.Clock);
        Block inbound = Block.Create(
            second.Id,
            subject.Id,
            subject.Username!.Value,
            factory.Clock);
        Block controlBlock = Block.Create(
            first.Id,
            control.Id,
            control.Username!.Value,
            factory.Clock);
        Notification recipient = Notification.Create(
            subject.Id,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(first.Id),
            factory.Clock);
        Notification actorOnly = Notification.Create(
            first.Id,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(subject.Id),
            factory.Clock);
        Notification controlNotification = Notification.Create(
            control.Id,
            NotificationType.FriendRequestReceived,
            NotificationSubject.ForActor(first.Id),
            factory.Clock);
        context.UsernameRegistry.Add(username);
        context.Friendships.AddRange(pending, accepted, controlFriendship);
        context.Blocks.AddRange(outbound, inbound, controlBlock);
        context.Notifications.AddRange(recipient, actorOnly, controlNotification);
        await context.SaveChangesAsync();
        return new Phase3DeletionSeed(
            subject.Id,
            first.Id,
            subjectEmail,
            subject.Username.Value.Value,
            username.NameCanonical,
            subject.DisplayName!.Value.Value,
            subject.FriendCode!.Value.Value,
            subject.GraceEndsAt!.Value,
            pending.Id,
            accepted.Id,
            controlFriendship.Id,
            outbound.Id,
            inbound.Id,
            controlBlock.Id,
            recipient.Id,
            actorOnly.Id,
            controlNotification.Id);
    }

    internal static ExecuteDeletionForUserUseCase CreateUseCase(
        IServiceProvider services,
        IUserRepository? users = null,
        IDeletionScopeProvider? scopeProvider = null,
        IDeletionResultApplier? resultApplier = null,
        IUserAccountGateway? accounts = null,
        ISessionLockRepository? sessionLocks = null,
        ISessionNotificationMaterializer? materializer = null,
        IPollNotificationMaterializer? pollMaterializer = null) =>
        new(
            users ?? services.GetRequiredService<IUserRepository>(),
            scopeProvider ?? services.GetRequiredService<IDeletionScopeProvider>(),
            resultApplier ?? services.GetRequiredService<IDeletionResultApplier>(),
            accounts ?? services.GetRequiredService<IUserAccountGateway>(),
            services.GetRequiredService<IDeletionJournal>(),
            new NoOpDeletionJournalShipper(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IClock>(),
            sessionLocks ?? services.GetRequiredService<ISessionLockRepository>(),
            materializer ?? services.GetRequiredService<ISessionNotificationMaterializer>(),
            pollMaterializer ?? services.GetRequiredService<IPollNotificationMaterializer>());
}

internal sealed class NoOpDeletionJournalShipper : IDeletionJournalShipper
{
    public Task<int> ShipPendingAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}
