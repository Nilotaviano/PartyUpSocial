using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.Security;

/// <summary>
/// A-071's opaque <c>version</c> token, backed directly by ASP.NET Data Protection's own
/// Protect/Unprotect so the mechanism is stable across every process sharing the persisted key ring:
/// <see cref="Issue"/> protects the <c>sessionId ‖ xmin</c> payload under a dedicated purpose; <see cref="Matches"/>
/// unprotects the incoming token and fixed-time-compares the recovered payload against the freshly
/// locked row's own <c>sessionId ‖ xmin</c> — never the token bytes against a value this process derived
/// for itself, which is what made the previous <c>Protect(new byte[32])</c>-as-key-derivation scheme
/// process-local (Data Protection's ciphertexts are non-deterministic per call, so no two processes, and
/// no two calls in the same process, ever agreed on that "key").
/// </summary>
public sealed class SessionVersionStamp(AppDbContext context, IDataProtectionProvider provider)
    : ISessionVersionStamp
{
    private const string Purpose = "PartyUp.Sessions.Version.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Issue(SessionId sessionId, uint xmin) =>
        Base64Url.EncodeToString(_protector.Protect(EncodePayload(sessionId, xmin)));

    public bool Matches(Session lockedSession, string token)
    {
        ArgumentNullException.ThrowIfNull(lockedSession);

        uint xmin = context.Entry(lockedSession).Property<uint>("xmin").CurrentValue;
        return Matches(lockedSession.Id, xmin, token);
    }

    internal bool Matches(SessionId sessionId, uint xmin, string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        byte[] candidatePayload;
        try
        {
            candidatePayload = _protector.Unprotect(Base64Url.DecodeFromChars(token));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }

        byte[] expectedPayload = EncodePayload(sessionId, xmin);
        return candidatePayload.Length == expectedPayload.Length
            && CryptographicOperations.FixedTimeEquals(candidatePayload, expectedPayload);
    }

    private static byte[] EncodePayload(SessionId sessionId, uint xmin)
    {
        byte[] payload = new byte[16 + sizeof(uint)];
        sessionId.Value.TryWriteBytes(payload);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16), xmin);
        return payload;
    }
}
