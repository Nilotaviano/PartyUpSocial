using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Friends;

public sealed class GetProfileUseCase(IUserGraphResolver resolver)
{
    public async Task<UsernameProfileResolution?> ExecuteAsync(
        UserId viewerId,
        string? username,
        CancellationToken cancellationToken)
    {
        string canonical = UsernameRegistry.Canonicalize(username);
        if (canonical.Length < Username.MinLength || canonical.Length > Username.MaxLength)
        {
            return null;
        }

        ResolvedUserGraph? resolved = await resolver.ResolveProfileByUsernameAsync(
            viewerId,
            canonical,
            cancellationToken);
        return resolved is null ? null : new UsernameProfileResolution(ProfileView.From(resolved));
    }
}

public sealed record UsernameProfileResolution(ProfileView Profile);

public abstract record ProfileView(string Disclosure, FriendRelationship Relationship)
{
    public static ProfileView From(ResolvedUserGraph resolved)
    {
        User user = resolved.User;
        string username = user.Username!.Value.Value;
        return resolved.Disclosure switch
        {
            ProfileDisclosure.UsernameOnly => new UsernameOnlyProfile(username, resolved.Relationship),
            ProfileDisclosure.Full => new FullProfile(
                user.Id,
                username,
                user.DisplayName!.Value.Value,
                user.Avatar!.Value,
                user.PlatformLinks
                    .Select(link => new PublicPlatform(link.Platform, link.Handle.Value))
                    .ToArray(),
                resolved.Relationship),
            _ => throw new InvalidOperationException("A hidden profile cannot be materialized."),
        };
    }
}

public sealed record UsernameOnlyProfile(
    string Username,
    FriendRelationship Relationship)
    : ProfileView("username_only", Relationship);

public sealed record FullProfile(
    UserId UserId,
    string Username,
    string DisplayName,
    Avatar Avatar,
    IReadOnlyList<PublicPlatform> Platforms,
    FriendRelationship Relationship)
    : ProfileView("full", Relationship);

public sealed record PublicPlatform(Platform Platform, string Handle);
