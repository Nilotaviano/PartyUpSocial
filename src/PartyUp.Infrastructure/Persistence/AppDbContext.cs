using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Deletion;
using PartyUp.Infrastructure.Notifications;
using PartyUp.Infrastructure.Persistence.Configurations;
using PartyUp.Infrastructure.Steam;
using PartyUp.Infrastructure.SteamCatalog;

namespace PartyUp.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AuthUser, AuthRole, Guid>(options)
{
    public DbSet<User> DomainUsers => Set<User>();

    public DbSet<AuthUser> AuthUsers => Set<AuthUser>();

    public DbSet<AdminSeedRefusal> AdminSeedRefusals => Set<AdminSeedRefusal>();

    public DbSet<PlatformLink> PlatformLinks => Set<PlatformLink>();

    public DbSet<AvatarSource> AvatarSources => Set<AvatarSource>();

    public DbSet<SteamIdentity> SteamIdentities => Set<SteamIdentity>();

    public DbSet<UsernameRegistryEntry> UsernameRegistry => Set<UsernameRegistryEntry>();

    public DbSet<DeletionJournalRecord> DeletionJournal => Set<DeletionJournalRecord>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<Block> Blocks => Set<Block>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<Session> Sessions => Set<Session>();

    public DbSet<Participation> SessionParticipations => Set<Participation>();

    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();

    public DbSet<GuestIdentity> GuestIdentities => Set<GuestIdentity>();

    public DbSet<GuestLink> GuestLinks => Set<GuestLink>();

    public DbSet<NotificationMute> NotificationMutes => Set<NotificationMute>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    public DbSet<NotificationPushSend> NotificationPushSends => Set<NotificationPushSend>();

    public DbSet<SessionReminderHandoff> SessionReminderHandoffs => Set<SessionReminderHandoff>();

    public DbSet<AvailabilityBlock> AvailabilityBlocks => Set<AvailabilityBlock>();

    public DbSet<AvailabilitySeries> AvailabilitySeries => Set<AvailabilitySeries>();

    public DbSet<AvailabilityOccurrenceSkip> AvailabilityOccurrenceSkips => Set<AvailabilityOccurrenceSkip>();

    public DbSet<Poll> Polls => Set<Poll>();

    public DbSet<PollOption> PollOptions => Set<PollOption>();

    public DbSet<PollParticipant> PollParticipants => Set<PollParticipant>();

    public DbSet<PollVote> PollVotes => Set<PollVote>();

    public DbSet<SteamCatalogApp> SteamApps => Set<SteamCatalogApp>();

    public DbSet<SteamCatalogStateRow> SteamCatalogState => Set<SteamCatalogStateRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // The schema has an exact index contract, so every required FK index is declared explicitly.
        configurationBuilder.Conventions.Remove<
            Microsoft.EntityFrameworkCore.Metadata.Conventions.ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new AuthUserConfiguration());
        builder.ApplyConfiguration(new AdminSeedRefusalConfiguration());
        builder.ApplyConfiguration(new AuthTokenConfiguration());
        builder.ApplyConfiguration(new DeletionJournalConfiguration());
        builder.ApplyConfiguration(new UserConfiguration());
        builder.ApplyConfiguration(new PlatformLinkConfiguration());
        builder.ApplyConfiguration(new AvatarSourceConfiguration());
        builder.ApplyConfiguration(new SteamIdentityConfiguration());
        builder.ApplyConfiguration(new UsernameRegistryEntryConfiguration());
        builder.Entity<User>().Navigation(user => user.PlatformLinks).AutoInclude();

        builder.Entity<AuthRole>().ToTable("auth_roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("auth_user_roles");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("auth_user_logins");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("auth_user_claims");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("auth_role_claims");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("auth_user_tokens");
        builder.Entity<IdentityUserRole<Guid>>()
            .HasIndex(userRole => userRole.RoleId)
            .HasDatabaseName("ix_auth_user_roles_role_id");
        builder.Entity<IdentityUserLogin<Guid>>()
            .HasIndex(login => login.UserId)
            .HasDatabaseName("ix_auth_user_logins_user_id");
        builder.Entity<IdentityUserClaim<Guid>>()
            .HasIndex(claim => claim.UserId)
            .HasDatabaseName("ix_auth_user_claims_user_id");
        builder.Entity<IdentityRoleClaim<Guid>>()
            .HasIndex(claim => claim.RoleId)
            .HasDatabaseName("ix_auth_role_claims_role_id");
        builder.Entity<AuthToken>()
            .HasIndex(token => token.UserId)
            .HasDatabaseName("ix_auth_tokens_user_id");
        builder.Ignore<IdentityUserPasskey<Guid>>();
        builder.ApplyConfiguration(new FriendshipConfiguration());
        builder.ApplyConfiguration(new BlockConfiguration());
        builder.ApplyConfiguration(new NotificationConfiguration());
        builder.ApplyConfiguration(new SessionConfiguration());
        builder.ApplyConfiguration(new SessionParticipationConfiguration());
        builder.ApplyConfiguration(new JoinRequestConfiguration());
        builder.ApplyConfiguration(new GuestIdentityConfiguration());
        builder.ApplyConfiguration(new GuestLinkConfiguration());
        builder.ApplyConfiguration(new NotificationMuteConfiguration());
        builder.ApplyConfiguration(new PushSubscriptionConfiguration());
        builder.ApplyConfiguration(new NotificationPushSendConfiguration());
        builder.ApplyConfiguration(new SessionReminderHandoffConfiguration());
        builder.ApplyConfiguration(new AvailabilityBlockConfiguration());
        builder.ApplyConfiguration(new AvailabilitySeriesConfiguration());
        builder.ApplyConfiguration(new AvailabilityOccurrenceSkipConfiguration());
        builder.ApplyConfiguration(new PollConfiguration());
        builder.ApplyConfiguration(new PollOptionConfiguration());
        builder.ApplyConfiguration(new PollParticipantConfiguration());
        builder.ApplyConfiguration(new PollVoteConfiguration());
        builder.ApplyConfiguration(new SteamCatalogAppConfiguration());
        builder.ApplyConfiguration(new SteamCatalogStateRowConfiguration());
    }
}
