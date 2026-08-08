using NodaTime;
using PartyUp.Api.Security;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// The A-064 web transport-entry exception (PHASE4_DESIGN.md §8.1/§9.1): <c>GET /g/{token}</c> sits
/// outside <c>/api/v1</c> and outside the OpenAPI document, makes no database write, and its sole
/// result is a protected cookie plus a redirect to a tokenless SPA route.
/// </summary>
public static class GuestExchangeEndpoints
{
    public static WebApplication MapGuestExchangeEndpoints(this WebApplication app)
    {
        app.MapGet("/g/{token}", ExchangeAsync)
            .WithName(ApiOperationIds.ExchangeGuestLink)
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> ExchangeAsync(
        string token,
        IGuestLinkResolver resolver,
        ISessionRepository sessions,
        IPollRepository polls,
        GuestLinkCredentialProtector protector,
        PollGuestLinkCredentialProtector pollProtector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        ClearStaleGuestCookies(context, protector, pollProtector, clock);

        ResolvedGuestLink? resolved = await resolver.ResolveAsync(token, cancellationToken);
        if (resolved is null)
        {
            return await ExchangePollBranchAsync(token, resolver, polls, pollProtector, clock, context, cancellationToken);
        }

        Session? session = await sessions.FindAsync(resolved.SessionId, cancellationToken);
        if (session is null)
        {
            return Results.Redirect("/guest/invalid");
        }

        GuestLinkView view;
        try
        {
            view = session.ResolveGuestLink(resolved.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return Results.Redirect("/guest/invalid");
        }

        Instant now = clock.GetCurrentInstant();
        bool isLive = view == GuestLinkView.LiveView;
        GuestCredentialMode mode = isLive ? GuestCredentialMode.Active : GuestCredentialMode.Terminal;
        Instant expiresAt = isLive
            ? MinInstant(now + GuestLinkCredentialProtector.MaxActiveLifetime, session.EndTime)
            : now + GuestLinkCredentialProtector.TerminalLifetime;

        var credential = new GuestLinkCredential(resolved.GuestLinkId, resolved.SessionId, mode, now, expiresAt);
        SetGuestCookie(context, protector, credential, expiresAt - now);

        string guestLinkIdN = resolved.GuestLinkId.Value.ToString("N");
        return Results.Redirect($"/guest/{guestLinkIdN}");
    }

    /// <summary>
    /// The poll-owned branch (PHASE6_DESIGN.md §4, ticket-P6-S4b), tried only once the session branch
    /// above has already missed: a link finalization already re-parented (row's <c>PollId</c> null,
    /// <c>SessionId</c> set) never reaches here — it matched <see cref="IGuestLinkResolver.ResolveAsync"/>
    /// instead, which is exactly what makes a re-exchanged token mint a session credential with no
    /// extra branching. Redirects to the SAME <c>/guest/{linkId}</c> SPA route the session branch does.
    /// </summary>
    private static async Task<IResult> ExchangePollBranchAsync(
        string token,
        IGuestLinkResolver resolver,
        IPollRepository polls,
        PollGuestLinkCredentialProtector pollProtector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ResolvedPollGuestLink? resolved = await resolver.ResolvePollAsync(token, cancellationToken);
        if (resolved is null)
        {
            return Results.Redirect("/guest/invalid");
        }

        Poll? poll = await polls.FindAsync(resolved.PollId, cancellationToken);
        if (poll is null)
        {
            return Results.Redirect("/guest/invalid");
        }

        GuestLinkView view;
        try
        {
            view = poll.ResolveGuestLink(resolved.GuestLinkId, clock);
        }
        catch (DomainException)
        {
            return Results.Redirect("/guest/invalid");
        }

        Instant now = clock.GetCurrentInstant();
        bool isLive = view == GuestLinkView.LiveView;
        GuestCredentialMode mode = isLive ? GuestCredentialMode.Active : GuestCredentialMode.Terminal;
        // Lifetime clamp (ticket-P6-S4b): active-mode expiry is the poll's own selection window
        // close — the poll is terminal from that instant on, so no separate 400-day ceiling applies
        // the way it does for a session's much-longer-lived link.
        Instant expiresAt = isLive
            ? poll.SelectionWindowEndsAt
            : now + PollGuestLinkCredentialProtector.TerminalLifetime;

        var credential = new PollGuestLinkCredential(resolved.GuestLinkId, resolved.PollId, mode, now, expiresAt);
        GuestPollEndpoints.SetPollGuestCookie(context, pollProtector, credential, expiresAt - now);

        string guestLinkIdN = resolved.GuestLinkId.Value.ToString("N");
        return Results.Redirect($"/guest/{guestLinkIdN}");
    }

    /// <summary>
    /// §9.1's stale-cookie bound: every <c>pu.g.*</c> cookie on the request whose payload is expired
    /// or whose 10-minute terminal window has closed is cleared via <c>Set-Cookie … Max-Age=0</c>.
    /// </summary>
    private static void ClearStaleGuestCookies(
        HttpContext context,
        GuestLinkCredentialProtector protector,
        PollGuestLinkCredentialProtector pollProtector,
        IClock clock)
    {
        foreach (string name in context.Request.Cookies.Keys)
        {
            if (!name.StartsWith("pu.g.", StringComparison.Ordinal))
            {
                continue;
            }

            string? value = context.Request.Cookies[name];
            if (value is null || IsStale(value, protector, pollProtector, clock))
            {
                context.Response.Cookies.Append(name, string.Empty, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = !context.RequestServices
                        .GetRequiredService<IWebHostEnvironment>().IsDevelopment(),
                    SameSite = SameSiteMode.Lax,
                    Path = StaleCookiePath(name),
                    MaxAge = TimeSpan.Zero,
                });
            }
        }
    }

    /// <summary>
    /// A cookie is stale when neither purpose can decrypt it (garbage/forged/wrong-purpose-for-a-
    /// deleted-link) or the one that does decrypt it reports itself expired — tried session-purpose
    /// first, then poll-purpose, since a genuine cookie only ever decrypts under one of the two.
    /// </summary>
    private static bool IsStale(
        string value,
        GuestLinkCredentialProtector protector,
        PollGuestLinkCredentialProtector pollProtector,
        IClock clock)
    {
        GuestLinkCredential? sessionCredential = protector.Unprotect(value);
        if (sessionCredential is not null)
        {
            return sessionCredential.IsExpired(clock);
        }

        PollGuestLinkCredential? pollCredential = pollProtector.Unprotect(value);
        return pollCredential is null || pollCredential.IsExpired(clock);
    }

    /// <summary>
    /// §9.1: browsers only clear a cookie when the delete <c>Set-Cookie</c> matches its original
    /// name AND path. The stale cookie's own name carries its guest link id (<c>pu.g.{id:N}</c>), so
    /// the path the credential was actually set with (<c>/api/v1/guest/{id:N}</c>) is reconstructed
    /// from it rather than defaulting to the request path.
    /// </summary>
    private static string StaleCookiePath(string cookieName) =>
        $"{ApiRoutes.V1}/guest/{cookieName["pu.g.".Length..]}";

    private static void SetGuestCookie(
        HttpContext context,
        GuestLinkCredentialProtector protector,
        GuestLinkCredential credential,
        Duration lifetime)
    {
        bool isDevelopment = context.RequestServices
            .GetRequiredService<IWebHostEnvironment>().IsDevelopment();
        string guestLinkIdN = credential.GuestLinkId.Value.ToString("N");
        context.Response.Cookies.Append(
            $"pu.g.{guestLinkIdN}",
            protector.Protect(credential),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !isDevelopment,
                SameSite = SameSiteMode.Lax,
                Path = $"{ApiRoutes.V1}/guest/{guestLinkIdN}",
                MaxAge = lifetime.ToTimeSpan() is { Ticks: > 0 } positive ? positive : TimeSpan.Zero,
            });
    }

    private static Instant MinInstant(Instant left, Instant right) => left < right ? left : right;
}
