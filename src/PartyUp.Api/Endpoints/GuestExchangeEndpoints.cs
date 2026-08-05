using NodaTime;
using PartyUp.Api.Security;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
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
        GuestLinkCredentialProtector protector,
        IClock clock,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        ClearStaleGuestCookies(context, protector, clock);

        ResolvedGuestLink? resolved = await resolver.ResolveAsync(token, cancellationToken);
        if (resolved is null)
        {
            return Results.Redirect("/guest/invalid");
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
    /// §9.1's stale-cookie bound: every <c>pu.g.*</c> cookie on the request whose payload is expired
    /// or whose 10-minute terminal window has closed is cleared via <c>Set-Cookie … Max-Age=0</c>.
    /// </summary>
    private static void ClearStaleGuestCookies(
        HttpContext context,
        GuestLinkCredentialProtector protector,
        IClock clock)
    {
        foreach (string name in context.Request.Cookies.Keys)
        {
            if (!name.StartsWith("pu.g.", StringComparison.Ordinal))
            {
                continue;
            }

            string? value = context.Request.Cookies[name];
            GuestLinkCredential? credential = value is null ? null : protector.Unprotect(value);
            if (credential is null || credential.IsExpired(clock))
            {
                context.Response.Cookies.Append(name, string.Empty, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = !context.RequestServices
                        .GetRequiredService<IWebHostEnvironment>().IsDevelopment(),
                    SameSite = SameSiteMode.Lax,
                    MaxAge = TimeSpan.Zero,
                });
            }
        }
    }

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
