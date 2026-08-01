using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Friends;

public sealed class ResolveFriendCodeUseCase(IUserGraphResolver resolver)
{
    public async Task<ResolveFriendCodeResult> ExecuteAsync(
        UserId viewerId,
        string? friendCode,
        CancellationToken cancellationToken)
    {
        string canonical = Normalize(friendCode);
        if (!FriendCodeFormat.IsWellFormed(canonical))
        {
            return ResolveFriendCodeResult.Invalid;
        }

        ResolvedUserGraph? resolved = await resolver.ResolveProfileByFriendCodeAsync(
            viewerId,
            canonical,
            cancellationToken);
        return resolved is null
            ? ResolveFriendCodeResult.Hidden
            : new FriendCodeFound(new FriendCodeResolution(ProfileView.From(resolved)));
    }

    public static string Normalize(string? friendCode) =>
        (friendCode?.Trim() ?? string.Empty)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}

public abstract record ResolveFriendCodeResult
{
    protected ResolveFriendCodeResult()
    {
    }

    public static ResolveFriendCodeResult Invalid { get; } = new FriendCodeInvalid();

    public static ResolveFriendCodeResult Hidden { get; } = new FriendCodeHidden();
}

public sealed record FriendCodeInvalid : ResolveFriendCodeResult;

public sealed record FriendCodeHidden : ResolveFriendCodeResult;

public sealed record FriendCodeFound(FriendCodeResolution Resolution) : ResolveFriendCodeResult;

public sealed record FriendCodeResolution(ProfileView Profile);
