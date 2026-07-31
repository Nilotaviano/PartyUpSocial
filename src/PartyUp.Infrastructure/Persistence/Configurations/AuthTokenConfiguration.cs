using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Auth;

namespace PartyUp.Infrastructure.Persistence.Configurations;

internal sealed class AuthTokenConfiguration : IEntityTypeConfiguration<AuthToken>
{
    public void Configure(EntityTypeBuilder<AuthToken> builder)
    {
        builder.ToTable("auth_tokens");
        builder.HasKey(token => token.Id);

        builder.Property(token => token.Id).ValueGeneratedNever();
        builder.Property(token => token.Purpose).IsRequired();
        builder.Property(token => token.TokenHash).IsRequired();
        builder.Property(token => token.UserId)
            .HasConversion(new ValueConverter<UserId?, Guid?>(
                value => value.HasValue ? value.Value.Value : null,
                value => value.HasValue ? UserId.From(value.Value) : null));
        builder.Property(token => token.EmailNormalized);
        builder.Property(token => token.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.ConsumedAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.RequestIpHash);
        builder.Property(token => token.PendingLocale);
        builder.Property(token => token.PendingAgeGate).HasDefaultValue(false);
        builder.Property(token => token.Payload).HasColumnType("jsonb");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(token => token.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(token => token.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_auth_tokens_token_hash");
        builder.HasIndex(token => token.ExpiresAt)
            .HasDatabaseName("ix_auth_tokens_expires_at");
        builder.HasIndex(token => token.EmailNormalized)
            .HasDatabaseName("ix_auth_tokens_email_normalized");
    }
}