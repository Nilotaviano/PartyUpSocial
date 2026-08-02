using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Friends;

public sealed class ListBlockedUsersUseCase(IBlockRepository blocks)
{
    public async Task<BlockedUserList> ExecuteAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BlockedUserProjection> rows =
            await blocks.ListOutboundAsync(userId, cancellationToken);
        return new BlockedUserList(rows
            .Select(row => new BlockedUserItem(
                row.BlockId,
                row.BlockedUsernameSnapshot,
                row.CreatedAt))
            .ToArray());
    }
}

public sealed record BlockedUserList(IReadOnlyList<BlockedUserItem> Items);

public sealed record BlockedUserItem(BlockId BlockId, string Username, Instant CreatedAt);
