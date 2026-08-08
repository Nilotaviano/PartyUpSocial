using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Sessions;

/// <summary>
/// The A-064 HTTP bearer-token exchange's resolver: token → (session, link) by the unique
/// <c>token_hash</c> digest (PHASE4_DESIGN.md §9.1). Session-owned links only — a poll-owned link's
/// token exchange is Phase 6. This performs no mutation and takes no lock: A-064 makes no database
/// write on a GET.
/// </summary>
public sealed class GuestLinkResolver(AppDbContext context) : IGuestLinkResolver
{
    public async Task<ResolvedGuestLink?> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string tokenHash;
        try
        {
            tokenHash = GuestLink.HashToken(token);
        }
        catch (DomainException)
        {
            return null;
        }

        var match = await context.GuestLinks
            .AsNoTracking()
            .Where(link => link.TokenHash == tokenHash && link.SessionId != null)
            .Select(link => new { link.Id, link.SessionId })
            .FirstOrDefaultAsync(cancellationToken);

        return match is null
            ? null
            : new ResolvedGuestLink(match.Id, match.SessionId!.Value);
    }

    /// <summary>
    /// The poll-owned analogue (ticket-P6-S4b): a link finalization already re-parented (its row's
    /// <c>PollId</c> now null, <c>SessionId</c> set) never matches here, so a re-exchanged token
    /// naturally falls through to <see cref="ResolveAsync"/> instead — no branching on poll status
    /// needed at the call site.
    /// </summary>
    public async Task<ResolvedPollGuestLink?> ResolvePollAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string tokenHash;
        try
        {
            tokenHash = GuestLink.HashToken(token);
        }
        catch (DomainException)
        {
            return null;
        }

        var match = await context.GuestLinks
            .AsNoTracking()
            .Where(link => link.TokenHash == tokenHash && link.PollId != null)
            .Select(link => new { link.Id, link.PollId })
            .FirstOrDefaultAsync(cancellationToken);

        return match is null
            ? null
            : new ResolvedPollGuestLink(match.Id, match.PollId!.Value);
    }
}
