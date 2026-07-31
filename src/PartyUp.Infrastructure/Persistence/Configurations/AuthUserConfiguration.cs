using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class AuthUserConfiguration : IEntityTypeConfiguration<AuthUser>
{
    public void Configure(EntityTypeBuilder<AuthUser> builder)
    {
        builder.ToTable("auth_users");

        // Identity owns a BCL model; the provider side stays native NodaTime under UseNodaTime().
        var lockoutEndConverter = new ValueConverter<DateTimeOffset?, Instant?>(
            value => value == null ? null : Instant.FromDateTimeOffset(value.Value),
            value => value == null ? null : value.Value.ToDateTimeOffset());

        builder.Property(user => user.LockoutEnd)
            .HasConversion(lockoutEndConverter)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(user => user.NormalizedEmail)
            .HasDatabaseName("ux_auth_users_normalized_email")
            .IsUnique()
            .HasFilter("normalized_email IS NOT NULL");
    }
}
