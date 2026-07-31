using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class UsernameRegistryEntryConfiguration : IEntityTypeConfiguration<UsernameRegistryEntry>
{
    public void Configure(EntityTypeBuilder<UsernameRegistryEntry> builder)
    {
        builder.ToTable("username_registry");
        builder.HasKey(entry => entry.NameCanonical);

        builder.Property(entry => entry.NameCanonical).ValueGeneratedNever();
        builder.Property(entry => entry.State).IsRequired();
        builder.Property(entry => entry.OwnerId)
            .HasConversion(new TypedIdConverter<UserId>());
        builder.Property(entry => entry.ReservedUntil)
            .HasColumnType("timestamp with time zone");

        builder.HasIndex(entry => entry.OwnerId)
            .IsUnique()
            .HasFilter("state = 0")
            .HasDatabaseName("ux_username_registry_owner_id_active");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.OwnerId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
