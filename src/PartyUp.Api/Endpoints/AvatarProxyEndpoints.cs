using PartyUp.Application.Abstractions;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// AVATARS_DESIGN.md §3.3: the picture proxy, an A-064 web transport-entry exception -- <c>GET
/// /a/{token}</c> sits outside <c>/api/v1</c> and outside the OpenAPI document, following the
/// <c>/g/{token}</c> precedent (<see cref="GuestExchangeEndpoints"/>). Pure token -> bytes: no
/// outbound call (capture already happened at write time, AVATARS_DESIGN.md §3.2) and no database
/// write on this GET.
/// </summary>
public static class AvatarProxyEndpoints
{
    public static WebApplication MapAvatarProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/a/{token}", GetAsync)
            .WithName(ApiOperationIds.GetAvatarProxy)
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> GetAsync(
        string token,
        IAvatarSourceRepository repository,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AvatarPicture? picture = await repository.FindByTokenAsync(token, cancellationToken);

        // §3.3: unknown token, deleted source and missing bytes are indistinguishable -- the same
        // 404, per A-064's rule.
        if (picture is null || picture.Bytes.Length == 0)
        {
            return Results.NotFound();
        }

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
        context.Response.Headers["Content-Disposition"] = "inline";
        context.Response.Headers.CacheControl = "private";

        return Results.Bytes(picture.Bytes, picture.ContentType);
    }
}
