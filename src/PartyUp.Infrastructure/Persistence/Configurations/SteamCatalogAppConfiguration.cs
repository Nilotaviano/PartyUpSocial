using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Infrastructure.SteamCatalog;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class SteamCatalogAppConfiguration : IEntityTypeConfiguration<SteamCatalogApp>
{
    public void Configure(EntityTypeBuilder<SteamCatalogApp> builder)
    {
        builder.ToTable("steam_apps");
        builder.HasKey(app => app.AppId);
        builder.Property(app => app.AppId).ValueGeneratedNever();
        builder.Property(app => app.Name).IsRequired();
        builder.Property(app => app.LastModified);

        // The GIN trigram index on lower(name) is raw SQL in the migration -- EF has no primitive
        // for a functional-expression GIN index (STEAM_CATALOG_DESIGN.md §2).
    }
}
