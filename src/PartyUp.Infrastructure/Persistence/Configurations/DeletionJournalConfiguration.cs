using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Infrastructure.Deletion;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

internal sealed class DeletionJournalConfiguration
    : IEntityTypeConfiguration<DeletionJournalRecord>
{
    public void Configure(EntityTypeBuilder<DeletionJournalRecord> builder)
    {
        builder.ToTable("deletion_journal");
        builder.HasKey(entry => entry.UserId);
        builder.Property(entry => entry.UserId)
            .HasConversion(new TypedIdConverter<PartyUp.Domain.Kernel.UserId>());
        builder.Property(entry => entry.ExecutedAt).HasColumnType("timestamp with time zone");
        builder.Property(entry => entry.ShippedAt).HasColumnType("timestamp with time zone");
        builder.HasIndex(entry => entry.ShippedAt)
            .HasDatabaseName("ix_deletion_journal_unshipped")
            .HasFilter("shipped_at IS NULL");
    }
}
