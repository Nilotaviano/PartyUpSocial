using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Security;

/// <summary>
/// The poll-owned guest bearer credential (PHASE6_DESIGN.md §4, ticket-P6-S4b): a parallel protector
/// to the shipped <see cref="GuestLinkCredentialProtector"/> under its own Data Protection purpose, so
/// a session-typed credential is structurally unreadable on a poll route and vice versa (different
/// purpose fails <c>Unprotect</c> outright, never a value-level check). Reuses
/// <see cref="GuestCredentialMode"/> — the two shapes carry the same terminal/active distinction, just
/// resolved against a different parent.
/// </summary>
public sealed record PollGuestLinkCredential(
    GuestLinkId GuestLinkId,
    PollId PollId,
    GuestCredentialMode Mode,
    Instant IssuedAt,
    Instant ExpiresAt)
{
    public bool IsExpired(IClock clock) => clock.GetCurrentInstant() >= ExpiresAt;
}

/// <summary>Serialization payload — <see cref="Instant"/> has no default <see cref="JsonSerializer"/> converter.</summary>
internal sealed record PollGuestLinkCredentialPayload(
    Guid GuestLinkId,
    Guid PollId,
    int Mode,
    long IssuedAtUnixTicks,
    long ExpiresAtUnixTicks);

public sealed class PollGuestLinkCredentialProtector
{
    /// <summary>The poll arm's own Data Protection purpose — never shared with the session protector.</summary>
    private const string Purpose = "PartyUp.Polls.GuestLinkCredential.v1";

    /// <summary>
    /// A-074's bounded terminal-window credential lifetime — reused byte-for-byte from the shipped
    /// session protector rather than duplicated, per the ticket's "terminal-notice mode reuses the
    /// shipped bounded terminal lifetime".
    /// </summary>
    public static readonly Duration TerminalLifetime = GuestLinkCredentialProtector.TerminalLifetime;

    private readonly IDataProtector _protector;

    public PollGuestLinkCredentialProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector(Purpose);

    public string Protect(PollGuestLinkCredential credential)
    {
        var payload = new PollGuestLinkCredentialPayload(
            credential.GuestLinkId.Value,
            credential.PollId.Value,
            (int)credential.Mode,
            credential.IssuedAt.ToUnixTimeTicks(),
            credential.ExpiresAt.ToUnixTimeTicks());
        return _protector.Protect(JsonSerializer.Serialize(payload, SerializerOptions));
    }

    /// <summary>Null for anything malformed, undecryptable (incl. a session-purpose cookie), or carrying an unknown mode — never throws.</summary>
    public PollGuestLinkCredential? Unprotect(string protectedPayload)
    {
        if (string.IsNullOrEmpty(protectedPayload))
        {
            return null;
        }

        try
        {
            string json = _protector.Unprotect(protectedPayload);
            PollGuestLinkCredentialPayload? payload =
                JsonSerializer.Deserialize<PollGuestLinkCredentialPayload>(json, SerializerOptions);
            if (payload is null || !Enum.IsDefined((GuestCredentialMode)payload.Mode))
            {
                return null;
            }

            return new PollGuestLinkCredential(
                GuestLinkId.From(payload.GuestLinkId),
                PollId.From(payload.PollId),
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
