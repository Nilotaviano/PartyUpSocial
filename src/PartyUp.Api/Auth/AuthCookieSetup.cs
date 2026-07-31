using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.Auth;

public static class AuthCookieSetup
{
    public static IServiceCollection AddPartyUpCookieAuthentication(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
            })
            .AddIdentityCookies();

        var identityBuilder = new IdentityBuilder(typeof(AuthUser), typeof(AuthRole), services);
        identityBuilder.AddSignInManager();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "pu.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.LoginPath = PathString.Empty;
            options.AccessDeniedPath = PathString.Empty;
            options.Events.OnRedirectToLogin = context =>
                WriteJsonFailureAsync(context.Response, StatusCodes.Status401Unauthorized, "unauthorized");
            options.Events.OnRedirectToAccessDenied = context =>
                WriteJsonFailureAsync(context.Response, StatusCodes.Status403Forbidden, "forbidden");
            options.Events.OnValidatePrincipal = context =>
                context.HttpContext.RequestServices
                    .GetRequiredService<ISecurityStampValidator>()
                    .ValidateAsync(context);
        });

        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.Zero);
        services.AddScoped<ISecurityStampValidator, PartyUpSecurityStampValidator>();

        return services;
    }

    private static Task WriteJsonFailureAsync(HttpResponse response, int statusCode, string code)
    {
        response.StatusCode = statusCode;
        return response.WriteAsJsonAsync(new { code });
    }
}
