using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class UnblockUserUseCaseTests
{
    [Fact]
    public async Task Execute_DeletesOnlyTheOutboundBlockAndRestoresNothing_D065()
    {
        IClock clock = FriendTestData.Clock();
        User blocker = FriendTestData.Active("unblock-actor", "Unblock Actor");
        User blocked = FriendTestData.Active("unblock-target", "Unblock Target");
        Block block = Block.Create(blocker.Id, blocked.Id, blocked.Username!.Value, clock);
        Session session = BlockExecutorTestData.Session(blocker.Id, blocked.Id, clock, makeGoing: true);
        _ = BlockEffects.Apply(block, [], [session], [], [], clock);
        _ = session.DequeueEvents();
        var calls = new List<string>();
        var blocks = new BlockTestBlockRepository(calls);
        blocks.Rows.Add(block);
        var useCase = new UnblockUserUseCase(
            new BlockTestPairLocker(blocker, blocked, calls),
            blocks,
            new BlockTestUnitOfWork());

        BlockGraphCommandResult result = await useCase.ExecuteAsync(
            blocker.Id,
            block.Id,
            default);

        Assert.True(result.Succeeded);
        Assert.False(result.Created);
        Assert.Empty(blocks.Rows);
        Assert.Equal(
            ParticipationStatus.Removed,
            session.Find(ParticipantRef.ForUser(blocked.Id))!.Status);
        Assert.Equal(["probe", "users", "block", "block-remove"], calls);
    }

    [Fact]
    public async Task Execute_CallsBlockEffectsUnblockWithoutLoadingEffectScope_D065()
    {
        IClock clock = FriendTestData.Clock();
        User blocker = FriendTestData.Active("no-scope-actor", "No Scope Actor");
        User blocked = FriendTestData.Active("no-scope-target", "No Scope Target");
        Block block = Block.Create(blocker.Id, blocked.Id, blocked.Username!.Value, clock);
        var calls = new List<string>();
        var blocks = new BlockTestBlockRepository(calls);
        blocks.Rows.Add(block);
        var useCase = new UnblockUserUseCase(
            new BlockTestPairLocker(blocker, blocked, calls),
            blocks,
            new BlockTestUnitOfWork());

        BlockGraphCommandResult result = await useCase.ExecuteAsync(
            blocker.Id,
            block.Id,
            default);

        Assert.True(result.Succeeded);
        Assert.Empty(blocks.Rows);
        Type[] dependencies = typeof(UnblockUserUseCase)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(IBlockEffectsExecutor), dependencies);
        Assert.DoesNotContain(typeof(IBlockEffectScopeProvider), dependencies);
    }
}
