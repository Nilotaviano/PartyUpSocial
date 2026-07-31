using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Email;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(configuration.GetConnectionString("Db"), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention());

        services.AddIdentityCore<AuthUser>(options =>
            {
                options.User.RequireUniqueEmail = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AuthRole>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.Replace(ServiceDescriptor.Scoped<
            IUserClaimsPrincipalFactory<AuthUser>,
            PartyUpUserClaimsPrincipalFactory>());

        services.AddScoped<IUserAccountGateway, UserAccountGateway>();
        services.AddScoped<IAccountSessionService, AccountSessionService>();
        services.AddScoped<ICurrentUserProjectionReader, CurrentUserProjectionReader>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISecurityTokenService, SecurityTokenService>();
        services.AddSingleton<IEmailTemplateRenderer, EmbeddedEmailTemplateRenderer>();
        services.AddSingleton<ChannelEmailDispatcher>();
        services.AddSingleton<IEmailDispatcher>(services =>
            services.GetRequiredService<ChannelEmailDispatcher>());
        services.AddSingleton<IEmailSender, LoggingEmailSender>();

        return services;
    }
}
