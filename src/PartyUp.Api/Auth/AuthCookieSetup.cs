using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PartyUp.Api.ErrorHandling;
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
                PartyUpProblemResults.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    "unauthorized",
                    context.HttpContext.RequestAborted);
            options.Events.OnRedirectToAccessDenied = context =>
                PartyUpProblemResults.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status403Forbidden,
                    "forbidden",
                    context.HttpContext.RequestAborted);
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
}
