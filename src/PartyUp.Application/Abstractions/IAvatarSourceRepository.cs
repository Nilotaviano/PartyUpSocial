using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Abstractions;

/// <summary>AVATARS_DESIGN.md §3.4's deletion tail: every avatar source row for a tombstoned user.</summary>
public interface IAvatarSourceRepository
{
    /// <summary>§11 step 5: every avatar source row for the user, purged inside the deletion transaction.</summary>
    Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken);

    /// <summary>
    /// §3.3's proxy lookup: a pure token -> bytes read, no tracking and no write (A-064). Null when
    /// the token is unknown or the source it named has since been removed -- both collapse to the
    /// same 404 at the endpoint, per A-064's indistinguishable-outcome rule.
    /// </summary>
    Task<AvatarPicture?> FindByTokenAsync(string token, CancellationToken cancellationToken);
}

/// <summary>The minimal projection the §3.3 proxy needs: never the full <c>AvatarSource</c> aggregate.</summary>
public sealed record AvatarPicture(byte[] Bytes, string ContentType);
