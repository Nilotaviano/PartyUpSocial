using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Application.Friends;
using PartyUp.Application.Text;
using PartyUp.Infrastructure.Accounts;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Email;
using PartyUp.Infrastructure.Friends;
using PartyUp.Infrastructure.Deletion;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using PartyUp.Infrastructure.Steam;

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
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<AuthRole>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.Replace(ServiceDescriptor.Scoped<
            IUserClaimsPrincipalFactory<AuthUser>,
            PartyUpUserClaimsPrincipalFactory>());

        services.AddScoped<IUserAccountGateway, UserAccountGateway>();
        services.AddScoped<IAdminSeedRefusalStore, AdminSeedRefusalStore>();
        services.AddScoped<IAccountSessionService, AccountSessionService>();
        services.AddScoped<ICurrentUserProjectionReader, CurrentUserProjectionReader>();
        services.AddScoped<IAdminUserReader, AdminUserReader>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUsernameRegistryRepository, UsernameRegistryRepository>();
        services.AddScoped<IFriendshipRepository, FriendshipRepository>();
        services.AddScoped<IBlockRepository, BlockRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IFriendPairLocker, FriendPairLocker>();
        services.AddScoped<IUserGraphResolver, UserGraphResolver>();
        services.AddScoped<IFriendCodeGenerator, FriendCodeGenerator>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<SendFriendRequestUseCase>();
        services.AddScoped<AcceptFriendRequestUseCase>();
        services.AddScoped<RejectFriendRequestUseCase>();
        services.AddScoped<CancelFriendRequestUseCase>();
        services.AddScoped<UnfriendUseCase>();
        services.AddSingleton<IPublicLinkBuilder>(new PublicLinkBuilder(
            configuration["PublicBaseUrl"] ?? "http://localhost:5173"));
        services.AddScoped<SecurityTokenService>();
        services.AddScoped<ISecurityTokenService>(provider =>
            provider.GetRequiredService<SecurityTokenService>());
        services.AddScoped<IAuthTokenRetentionPurger>(provider =>
            provider.GetRequiredService<SecurityTokenService>());
        services.AddScoped<IDeletionScopeProvider, DeletionScopeProvider>();
        services.AddScoped<IDeletionJournal, DeletionJournal>();
        services.AddScoped<IDeletionJournalShipper, FileDeletionJournalShipper>();
        services.AddSingleton(new DeletionJournalOptions(
            configuration["Deletion:JournalPath"] ?? "/data/deletion-journal"));
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        services.AddSingleton<SteamOpenIdVerifier>();
        services.AddSingleton<SteamApiClient>();
        services.AddSingleton<ISteamProfileClient>(provider =>
            new SteamProfileClient(
                provider.GetRequiredService<SteamApiClient>(),
                configuration["Steam:ApiKey"]));
        services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);
        services.AddSingleton<IEmailTemplateRenderer, EmbeddedEmailTemplateRenderer>();
        services.AddSingleton<ChannelEmailDispatcher>();
        services.AddSingleton<IEmailDispatcher>(services =>
            services.GetRequiredService<ChannelEmailDispatcher>());
        services.AddSingleton<ITextPolicy, ProfanityFilter>();
        services.AddSingleton<IEmailSender>(provider =>
        {
            string configured = configuration["Email:Provider"]?.Trim() ?? "log";
            return configured.ToLowerInvariant() switch
            {
                "log" => ActivatorUtilities.CreateInstance<LoggingEmailSender>(provider),
                "smtp" => ActivatorUtilities.CreateInstance<SmtpEmailSender>(provider),
                _ => throw new InvalidOperationException(
                    $"Unsupported Email:Provider '{configured}'."),
            };
        });

        return services;
    }
}
