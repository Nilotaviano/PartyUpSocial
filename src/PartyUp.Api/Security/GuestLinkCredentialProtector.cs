using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Security;

/// <summary>The A-065 credential's two shapes: normal vs the bounded terminal-window credential (A-074).</summary>
public enum GuestCredentialMode
{
    Active,
    Terminal,
}

/// <summary>
/// The A-065 guest bearer credential: <c>{GuestLinkId, Mode, IssuedAt, ExpiresAt}</c>, protected under
/// a dedicated Data Protection purpose. There is deliberately no version field — <c>guest_links.state</c>
/// is the single revocation authority (A-065), so the credential carries only what the resolver needs
/// to decide the A-074 branch: which link, which mode, and when it expires.
/// <see cref="SessionId"/> is carried alongside for routing only: the shipped
/// <c>IGuestLinkResolver</c> (S1) resolves a raw bearer token to (link, session) once, at the A-064
/// exchange; every subsequent <c>/api/v1/guest/{guestLinkId}/...</c> call carries no token, only this
/// credential, and no shipped Application port resolves a bare <c>GuestLinkId</c> back to its session.
/// Caching the session id here — never re-derived from anything guessable — avoids widening that port.
/// </summary>
public sealed record GuestLinkCredential(
    GuestLinkId GuestLinkId,
    SessionId SessionId,
    GuestCredentialMode Mode,
    Instant IssuedAt,
    Instant ExpiresAt)
{
    public bool IsExpired(IClock clock) => clock.GetCurrentInstant() >= ExpiresAt;
}

/// <summary>Serialization payload — <see cref="Instant"/> has no default <see cref="JsonSerializer"/> converter.</summary>
internal sealed record GuestLinkCredentialPayload(
    Guid GuestLinkId,
    Guid SessionId,
    int Mode,
    long IssuedAtUnixTicks,
    long ExpiresAtUnixTicks);

public sealed class GuestLinkCredentialProtector
{
    /// <summary>A-065's dedicated Data Protection purpose — never shared with any other protector.</summary>
    private const string Purpose = "PartyUp.Sessions.GuestLinkCredential.v1";

    /// <summary>§9.1: the maximum lifetime of an <c>active</c>-mode credential.</summary>
    public static readonly Duration MaxActiveLifetime = Duration.FromDays(400);

    /// <summary>A-074's bounded terminal-window credential lifetime.</summary>
    public static readonly Duration TerminalLifetime = Duration.FromMinutes(10);

    private readonly IDataProtector _protector;

    public GuestLinkCredentialProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose);

    public string Protect(GuestLinkCredential credential)
    {
        var payload = new GuestLinkCredentialPayload(
            credential.GuestLinkId.Value,
            credential.SessionId.Value,
            (int)credential.Mode,
            credential.IssuedAt.ToUnixTimeTicks(),
            credential.ExpiresAt.ToUnixTimeTicks());
        return _protector.Protect(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    /// <summary>Null for anything malformed, undecryptable, or carrying an unknown mode — never throws.</summary>
    public GuestLinkCredential? Unprotect(string protectedPayload)
    {
        if (string.IsNullOrEmpty(protectedPayload))
        {
            return null;
        }

        try
        {
            string json = _protector.Unprotect(protectedPayload);
            GuestLinkCredentialPayload? payload = JsonSerializer.Deserialize<GuestLinkCredentialPayload>(json, SerializerOptions);
            if (payload is null || !Enum.IsDefined((GuestCredentialMode)payload.Mode))
            {
                return null;
            }

            return new GuestLinkCredential(
                GuestLinkId.From(payload.GuestLinkId),
                SessionId.From(payload.SessionId),
                (GuestCredentialMode)payload.Mode,
                Instant.FromUnixTimeTicks(payload.IssuedAtUnixTicks),
                Instant.FromUnixTimeTicks(payload.ExpiresAtUnixTicks));
        }
        catch (Exception exception) when (
            exception is System.Security.Cryptography.CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
