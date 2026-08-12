using PartyUp.Domain.Users;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// AVATARS_DESIGN.md §3.2: captures an upstream avatar picture at write time -- https-only, host
/// allowlisted per <see cref="AvatarSourceKind"/>, size-capped, magic-byte sniffed and re-encoded
/// server-side. Never called on the read path (the proxy makes no outbound call).
/// </summary>
public interface IAvatarCaptureService
{
    /// <summary>
    /// Returns the re-encoded bytes and their server-chosen content type, or <c>null</c> when the
    /// upstream URL fails any of §3.2's steps 1-5 (scheme, host allowlist, redirect, size cap,
    /// magic-byte sniff). A <c>null</c> result is an expected outcome, not a fault -- callers must
    /// still guard unexpected exceptions (network failures, decode failures) so capture never fails
    /// the sign-in it rides along with.
    /// </summary>
    Task<AvatarCapture?> CaptureAsync(
        string upstreamUrl,
        AvatarSourceKind kind,
        CancellationToken cancellationToken);
}

/// <summary>Re-encoded 128px WebP bytes ready for <see cref="Domain.Users.User.CaptureAvatarSource"/>.</summary>
public sealed record AvatarCapture(byte[] Bytes, string ContentType);
