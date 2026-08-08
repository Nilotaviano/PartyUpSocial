using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>Shared construction helpers for the Phase 6 S2 poll command integration tests.</summary>
internal static class PollTestHelpers
{
    internal static PollDetails DetailsFor(string title = "S2 poll test", int? maxCapacity = null) => new(
        SessionTitle.Create(title),
        Duration15.FromMinutes(60),
        Platform.NotApplicable,
        null,
        SessionNotes.Empty,
        minAttendance: 1,
        maxCapacity: maxCapacity,
        isPrivate: false);

    internal static CreatePollUseCase BuildCreateUseCase(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static PollOptionUseCases BuildOptionUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static PollVoteUseCases BuildVoteUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static PollGuestManagementUseCases BuildGuestManagementUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static GuestPollUseCases BuildGuestUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static PollJoinRequestUseCases BuildJoinRequestUseCases(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IFriendshipRepository>(),
        scope.ServiceProvider.GetRequiredService<IBlockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static CancelPollUseCase BuildCancelUseCase(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    internal static FinalizePollUseCase BuildFinalizeUseCase(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<IPollRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollLockRepository>(),
        scope.ServiceProvider.GetRequiredService<ISessionRepository>(),
        scope.ServiceProvider.GetRequiredService<IPollNotificationMaterializer>(),
        scope.ServiceProvider.GetRequiredService<ISessionReminderHandoffWriter>(),
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
        scope.ServiceProvider.GetRequiredService<IClock>());

    /// <summary>Seeds an accepted friendship between organizer and counterpart so D-125's create-time check passes.</summary>
    internal static async Task SeedAcceptedFriendshipAsync(
        AppDbContext context,
        UserId organizerId,
        UserId counterpartId,
        IClock clock)
    {
        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == organizerId),
            await context.DomainUsers.SingleAsync(u => u.Id == counterpartId),
            clock);
        friendship.Accept(counterpartId, clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
    }
}
