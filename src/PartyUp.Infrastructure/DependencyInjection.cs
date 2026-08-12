using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Availability;
using PartyUp.Application.Email;
using PartyUp.Application.Friends;
using PartyUp.Application.Notifications;
using PartyUp.Application.Polls;
using PartyUp.Application.Push;
using PartyUp.Application.Sessions;
using PartyUp.Application.SteamCatalog;
using PartyUp.Application.Text;
using PartyUp.Infrastructure.Accounts;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Avatars;
using PartyUp.Infrastructure.Email;
using PartyUp.Infrastructure.Friends;
using PartyUp.Infrastructure.Deletion;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
using PartyUp.Infrastructure.Push;
using PartyUp.Infrastructure.Sessions;
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
        services.AddScoped<ISteamImportAccountGateway, UserAccountGateway>();
        services.AddScoped<IAdminSeedRefusalStore, AdminSeedRefusalStore>();
        services.AddScoped<IAccountSessionService, AccountSessionService>();
        services.AddScoped<ICurrentUserProjectionReader, CurrentUserProjectionReader>();
        services.AddScoped<IAdminUserReader, AdminUserReader>();
        services.AddScoped<IUsageDashboardReader, UsageDashboardReader>();
        services.AddScoped<IUserActivityWriter, UserActivityWriter>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IAvatarSourceRepository, AvatarSourceRepository>();
        services.AddScoped<IUsernameRegistryRepository, UsernameRegistryRepository>();
        services.AddScoped<IFriendshipRepository, FriendshipRepository>();
        services.AddScoped<IBlockRepository, BlockRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IPushSubscriptionRepository, PushSubscriptionRepository>();
        services.AddScoped<INotificationPushSendRepository, NotificationPushSendRepository>();
        services.AddScoped<INotificationMuteRepository, NotificationMuteRepository>();
        services.AddScoped<INotificationDispatchRepository, NotificationDispatchRepository>();
        services.AddScoped<NotificationReadUseCases>();
        // PHASE7_DESIGN.md §4 (S4): HTTP-surface use cases + their read repositories.
        services.AddScoped<INotificationReadRepository, NotificationReadRepository>();
        services.AddScoped<IPushPromptEligibilityRepository, PushPromptEligibilityRepository>();
        services.AddScoped<ListNotificationsUseCase>();
        services.AddScoped<MarkNotificationReadUseCase>();
        services.AddScoped<MarkAllNotificationsReadUseCase>();
        services.AddScoped<SetNotificationMuteUseCase>();
        services.AddScoped<CreatePushSubscriptionUseCase>();
        services.AddScoped<DeletePushSubscriptionUseCase>();
        services.AddScoped<IFriendPairLocker, FriendPairLocker>();
        services.AddScoped<IUserGraphResolver, UserGraphResolver>();
        services.AddScoped<IFriendCodeGenerator, FriendCodeGenerator>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<ISessionReadRepository, SessionReadRepository>();
        services.AddScoped<IAvailabilityRepository, AvailabilityRepository>();
        services.AddScoped<IAvailabilityReadRepository, AvailabilityReadRepository>();
        services.AddScoped<ISessionLockRepository, SessionLockRepository>();
        services.AddScoped<IPollRepository, PollRepository>();
        services.AddScoped<IPollReadRepository, PollReadRepository>();
        services.AddScoped<IPollLockRepository, PollLockRepository>();
        services.AddScoped<IPollNotificationMaterializer, PollNotificationMaterializer>();
        services.AddScoped<CreatePollUseCase>();
        services.AddScoped<PollOptionUseCases>();
        services.AddScoped<PollVoteUseCases>();
        services.AddScoped<PollGuestManagementUseCases>();
        services.AddScoped<PollInvitationUseCases>();
        services.AddScoped<GuestPollUseCases>();
        services.AddScoped<PollJoinRequestUseCases>();
        services.AddScoped<PollReadUseCases>();
        services.AddScoped<GuestPollReadUseCases>();
        services.AddScoped<CancelPollUseCase>();
        services.AddScoped<FinalizePollUseCase>();
        services.AddScoped<PollTransitionUseCase>();
        services.AddScoped<ListPollsUseCase>();
        services.AddScoped<PollFinalizationPreviewUseCase>();
        services.AddScoped<IBlockEffectsExecutor, BlockEffectsExecutor>();
        services.AddScoped<IBlockEffectScopeProvider, BlockEffectScopeProvider>();
        services.AddScoped<IReminderScheduleWriter, SessionReminderScheduleWriter>();
        services.AddScoped<IDeletionResultApplier, DeletionResultApplier>();
        services.AddScoped<ISessionNotificationMaterializer, SessionNotificationMaterializer>();
        services.AddScoped<ISessionReminderHandoffWriter, SessionReminderHandoffStore>();
        services.AddScoped<IGuestLinkResolver, PartyUp.Infrastructure.Sessions.GuestLinkResolver>();
        services.AddScoped<CreateSessionUseCase>();
        services.AddScoped<EditSessionUseCase>();
        services.AddScoped<SessionInvitationUseCases>();
        services.AddScoped<SessionParticipationUseCases>();
        services.AddScoped<SessionJoinRequestUseCases>();
        services.AddScoped<SetSessionReminderUseCase>();
        services.AddScoped<SessionReadUseCases>();
        services.AddScoped<GuestSessionUseCases>();
        services.AddScoped<SessionTransitionUseCase>();
        services.AddScoped<SessionBlockEffectsUseCase>();
        services.AddScoped<CreateAvailabilityBlockUseCase>();
        services.AddScoped<EditAvailabilityBlockUseCase>();
        services.AddScoped<DeleteAvailabilityBlockUseCase>();
        services.AddScoped<CreateAvailabilitySeriesUseCase>();
        services.AddScoped<DeleteAvailabilitySeriesUseCase>();
        services.AddScoped<EditAvailabilityOccurrenceUseCase>();
        services.AddScoped<DeleteAvailabilityOccurrenceUseCase>();
        services.AddScoped<ReadMyAvailabilityUseCase>();
        services.AddScoped<FindOverlapWindowsUseCase>();
        services.AddScoped<GetHomeSuggestionsUseCase>();
        services.AddScoped<GetCalendarViewUseCase>();
        services.AddScoped<ListDiscoverableSessionsUseCase>();
        services.AddScoped<GetActionableInboxUseCase>();
        services.AddScoped<SessionDueCandidateStore>();
        services.AddScoped<SessionDueSweepJob>();
        services.AddScoped<SessionReminderDueJob>();
        services.AddScoped<PollDueCandidateStore>();
        services.AddScoped<PollDueSweepJob>();
        services.AddScoped<PollDeadlineApproachingJob>();
        services.AddScoped<PushPayloadFactory>();
        services.AddScoped<PushDispatchJob>();
        services.AddScoped<PushSendLogPruneJob>();
        // SessionDeletionEffectsUseCase depends on ExecuteDeletionForUserUseCase; both are registered
        // here (rather than split with Program.cs) so a caller only needs to add IClock (registered in
        // Program.cs, not here) alongside AddInfrastructure to resolve the whole graph.
        services.AddScoped<PartyUp.Application.Deletion.ExecuteDeletionForUserUseCase>();
        services.AddScoped<SessionDeletionEffectsUseCase>();
        services.AddScoped<SendFriendRequestUseCase>();
        services.AddScoped<AcceptFriendRequestUseCase>();
        services.AddScoped<RejectFriendRequestUseCase>();
        services.AddScoped<CancelFriendRequestUseCase>();
        services.AddScoped<UnfriendUseCase>();
        services.AddScoped<BlockUserUseCase>();
        services.AddScoped<UnblockUserUseCase>();
        services.AddScoped<ImportSteamFriendsUseCase>();
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
        services.AddSingleton(_ =>
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PartyUp/1.0 (+https://partyup.social)");
            return httpClient;
        });
        services.AddSingleton<SteamOpenIdVerifier>();
        services.AddSingleton<SteamApiClient>();
        services.AddSingleton<ISteamProfileClient>(provider =>
            new SteamProfileClient(
                provider.GetRequiredService<SteamApiClient>(),
                configuration["Steam:ApiKey"]));
        services.AddSingleton<ISteamFriendListClient>(provider =>
            new SteamFriendListClient(
                provider.GetRequiredService<SteamApiClient>(),
                configuration["Steam:ApiKey"]));
        services.AddSingleton<ISteamAppListClient>(provider =>
            new SteamAppListClient(
                provider.GetRequiredService<SteamApiClient>(),
                configuration["Steam:ApiKey"]));
        services.AddScoped<ISteamCatalogRepository, SteamCatalogRepository>();
        services.AddScoped<SteamCatalogRefreshJob>();
        services.AddScoped<SearchSteamCatalogUseCase>();
        // A dedicated instance, never the shared singleton HttpClient above -- AVATARS_DESIGN.md §3.2.
        services.AddSingleton<IAvatarCaptureService, AvatarCaptureService>();
        services.AddScoped<AvatarRefreshJob>();
        services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);
        services.AddSingleton<IEmailTemplateRenderer, EmbeddedEmailTemplateRenderer>();
        services.AddSingleton<IPushTemplateRenderer, EmbeddedPushTemplateRenderer>();
        services.AddSingleton<ChannelEmailDispatcher>();
        services.AddSingleton<IEmailDispatcher>(services =>
            services.GetRequiredService<ChannelEmailDispatcher>());
        services.AddScoped<IEmailRecipientReader, EmailRecipientReader>();
        services.AddScoped<InviteEmailService>();
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
        services.AddSingleton<IPushSender>(provider =>
        {
            string configured = configuration["Push:Provider"]?.Trim() ?? "log";
            return configured.ToLowerInvariant() switch
            {
                "log" => ActivatorUtilities.CreateInstance<LoggingPushSender>(provider),
                "webpush" => ActivatorUtilities.CreateInstance<WebPushSender>(provider),
                _ => throw new InvalidOperationException(
                    $"Unsupported Push:Provider '{configured}'."),
            };
        });

        return services;
    }
}
