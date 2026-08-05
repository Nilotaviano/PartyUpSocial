using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class GuestLinkConfiguration : IEntityTypeConfiguration<GuestLink>
{
    public void Configure(EntityTypeBuilder<GuestLink> builder)
    {
        builder.ToTable("guest_links", table => table.HasCheckConstraint("ck_guest_links_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)"));
        builder.HasKey(link => link.Id);
        builder.Property(link => link.Id).HasConversion(new TypedIdConverter<GuestLinkId>()).ValueGeneratedNever();
        builder.Property(link => link.SessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(link => link.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(link => link.GuestIdentityId).HasConversion(new TypedIdConverter<GuestIdentityId>());
        builder.Property(link => link.TokenHash).HasMaxLength(43);
        builder.Property(link => link.State).HasConversion<short>().HasColumnType("smallint");
        builder.Property(link => link.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(link => link.ClaimedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(link => link.IsActive);
        builder.Ignore(link => link.IsClaimed);
        builder.HasIndex(link => link.TokenHash).IsUnique().HasDatabaseName("ux_guest_links_token_hash");
        builder.HasIndex(link => new { link.SessionId, link.State }).HasDatabaseName("ix_guest_links_session_state");
        builder.HasOne<GuestIdentity>().WithMany().HasForeignKey(link => link.GuestIdentityId).OnDelete(DeleteBehavior.Restrict);
    }
}
