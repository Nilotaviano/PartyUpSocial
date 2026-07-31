using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// A linked gaming platform and its handle (D-032, DOMAIN_MODEL §3). Distinct from auth-method
/// linking (D-087/D-137), which is Phase 2 identity. Created through <see cref="User.LinkPlatform"/>.
/// </summary>
public sealed class PlatformLink
{
    private PlatformLink()
    {
    }

    private PlatformLink(PlatformLinkId id, UserId userId, Platform platform, GamingHandle handle)
    {
        Id = id;
        UserId = userId;
        Platform = platform;
        Handle = handle;
    }

    public PlatformLinkId Id { get; private set; }

    public UserId UserId { get; private set; }

    public Platform Platform { get; private set; }

    public GamingHandle Handle { get; private set; }

    internal static PlatformLink Create(UserId userId, Platform platform, GamingHandle handle)
    {
        if (!Enum.IsDefined(platform))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown platform '{platform}'."));
        }

        if (platform == Kernel.Platform.NotApplicable)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "'Not applicable' is a session platform choice, not a linkable gaming platform.");
        }

        if (handle.Value.Length == 0)
        {
            throw new DomainException(DomainError.ValidationFailed, "A platform link requires a handle.");
        }

        return new PlatformLink(PlatformLinkId.New(), userId, platform, handle);
    }

    internal void ChangeHandle(GamingHandle handle)
    {
        if (handle.Value.Length == 0)
        {
            throw new DomainException(DomainError.ValidationFailed, "A platform link requires a handle.");
        }

        Handle = handle;
    }
}
