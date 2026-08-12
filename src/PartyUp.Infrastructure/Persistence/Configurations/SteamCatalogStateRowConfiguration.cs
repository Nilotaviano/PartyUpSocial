using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Infrastructure.SteamCatalog;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class SteamCatalogStateRowConfiguration : IEntityTypeConfiguration<SteamCatalogStateRow>
{
    public void Configure(EntityTypeBuilder<SteamCatalogStateRow> builder)
    {
        builder.ToTable("steam_catalog_state");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Watermark);
        builder.Property(row => row.LastRunAt);
        builder.Property(row => row.LastRunSucceeded).IsRequired();
    }
}
