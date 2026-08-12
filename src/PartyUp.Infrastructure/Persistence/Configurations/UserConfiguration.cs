using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Id)
            .HasConversion(new TypedIdConverter<UserId>())
            .ValueGeneratedNever();
        builder.Property(user => user.Lifecycle).IsRequired();
        builder.Property(user => user.Username)
            .HasConversion(new TextValueConverter<Username>());
        builder.Property(user => user.DisplayName)
            .HasConversion(new TextValueConverter<DisplayName>());
        builder.Property(user => user.TimeZone)
            .HasConversion<DateTimeZoneConverter>()
            .HasColumnName("time_zone_id");
        builder.Property(user => user.Locale)
            .HasConversion(new TextValueConverter<Locale>());
        builder.Property(user => user.FriendCode)
            .HasConversion(new TextValueConverter<FriendCode>());
        builder.Property(user => user.AgeGatePassed).IsRequired();
        builder.Property(user => user.AgeGateSource);
        builder.Property(user => user.ReminderDefault).IsRequired();
        builder.Property(user => user.EmailNotificationsEnabled)
            .IsRequired()
            .HasDefaultValue(true);
        builder.Property(user => user.CreatedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.UsernameChangedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.GraceEndsAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.DeletedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.BannedAt)
            .HasColumnType("timestamp with time zone");
        builder.Property(user => user.TourCompletedAt)
            .HasColumnType("timestamp with time zone");

        // PHASE8_DESIGN.md §2 Q1: last-seen is operator telemetry the D-178 dashboard reads, never a
        // domain input — it stays a shadow property so no aggregate invariant can come to depend on
        // it, and the throttled touch writes it with SQL outside the change tracker.
        builder.Property<Instant?>(UserActivityWriter.LastSeenProperty)
            .HasColumnName("last_seen_at")
            .HasColumnType("timestamp with time zone");

        builder.ComplexProperty(user => user.Avatar, avatar =>
        {
            avatar.Property(value => value.Kind).HasColumnName("avatar_kind");
            avatar.Property(value => value.Emoji).HasColumnName("avatar_emoji");
            avatar.Property(value => value.SourceId)
                .HasConversion(new TypedIdConverter<AvatarSourceId>())
                .HasColumnName("avatar_source_id");
            // AVATARS_DESIGN.md §3.1/§3.3: the proxy token denormalized onto the users row so
            // AvatarResponse.From(Avatar) stays a pure, join-free mapper. Deliberately not
            // store-generated: a default would make EF propagate the read-back value into the
            // Nullable<Avatar> complex struct, whose fields its setter factory cannot write.
            avatar.Property(value => value.Token)
                .HasColumnName("avatar_token");
        });

        builder.Navigation(user => user.PlatformLinks)
            .HasField("_platformLinks")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Navigation(user => user.AvatarSources)
            .HasField("_avatarSources")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(user => user.IsOnboarding);
        builder.Ignore(user => user.IsActive);
        builder.Ignore(user => user.IsPendingDeletion);
        builder.Ignore(user => user.IsTombstoned);
        builder.Ignore(user => user.IsBanned);
        builder.Ignore(user => user.IsDiscoverable);
        builder.Ignore(user => user.CanBeFriended);
        builder.Ignore(user => user.IsProfileComplete);

        builder.HasIndex(user => user.Lifecycle)
            .HasDatabaseName("ix_users_lifecycle");
        builder.HasIndex(user => user.GraceEndsAt)
            .HasFilter("lifecycle = 2")
            .HasDatabaseName("ix_users_grace_ends_at");
        builder.HasIndex(user => user.BannedAt)
            .HasFilter("banned_at IS NOT NULL")
            .HasDatabaseName("ix_users_banned_at");

        builder.Property<uint>("xmin").IsRowVersion();
    }
}
