using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
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

        builder.ComplexProperty(user => user.Avatar, avatar =>
        {
            avatar.Property(value => value.Kind).HasColumnName("avatar_kind");
            avatar.Property(value => value.Emoji).HasColumnName("avatar_emoji");
            avatar.Property(value => value.SourcePlatformLinkId)
                .HasConversion(new TypedIdConverter<PlatformLinkId>())
                .HasColumnName("avatar_source_platform_link_id");
        });

        builder.Navigation(user => user.PlatformLinks)
            .HasField("_platformLinks")
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
