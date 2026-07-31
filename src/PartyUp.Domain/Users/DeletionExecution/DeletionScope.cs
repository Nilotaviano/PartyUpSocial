using PartyUp.Domain.Friendships;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Users.DeletionExecution;

/// <summary>
/// Everything the deletion executor is allowed to touch (DOMAIN_MODEL §11), loaded and locked by the
/// application layer in one transaction. <see cref="Sessions"/> and <see cref="Polls"/> hold both the
/// user's own content and other people's content the user takes part in — the executor splits them
/// by organizer, because the two halves get opposite treatment (steps 1–2 versus step 3).
/// </summary>
public sealed record DeletionScope(
    IReadOnlyList<Session> Sessions,
    IReadOnlyList<Poll> Polls,
    IReadOnlyList<Friendship> Friendships,
    IReadOnlyList<Block> Blocks,
    UsernameRegistryEntry? UsernameEntry)
{
    public static DeletionScope Empty { get; } = new([], [], [], [], null);
}
