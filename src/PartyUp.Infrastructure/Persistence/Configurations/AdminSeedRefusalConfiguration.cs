using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Infrastructure.Persistence.Configurations;

internal sealed class AdminSeedRefusalConfiguration
    : IEntityTypeConfiguration<AdminSeedRefusal>
{
    public void Configure(EntityTypeBuilder<AdminSeedRefusal> builder)
    {
        builder.ToTable("admin_seed_refusals");
        builder.HasKey(refusal => refusal.UserId);
        builder.Property(refusal => refusal.RefusedAt)
            .HasColumnType("timestamp with time zone");
        builder.HasOne<AuthUser>()
            .WithOne()
            .HasForeignKey<AdminSeedRefusal>(refusal => refusal.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
