using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
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
        IUserAccountGateway? accounts = null) =>
        new(
            users ?? services.GetRequiredService<IUserRepository>(),
            scopeProvider ?? services.GetRequiredService<IDeletionScopeProvider>(),
            resultApplier ?? services.GetRequiredService<IDeletionResultApplier>(),
            accounts ?? services.GetRequiredService<IUserAccountGateway>(),
            services.GetRequiredService<IDeletionJournal>(),
            new NoOpDeletionJournalShipper(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IClock>());
}

internal sealed class NoOpDeletionJournalShipper : IDeletionJournalShipper
{
    public Task<int> ShipPendingAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}
