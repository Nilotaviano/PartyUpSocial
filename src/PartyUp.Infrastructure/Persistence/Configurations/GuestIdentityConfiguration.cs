using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class GuestIdentityConfiguration : IEntityTypeConfiguration<GuestIdentity>
{
    public void Configure(EntityTypeBuilder<GuestIdentity> builder)
    {
        builder.ToTable("guest_identities", table => table.HasCheckConstraint("ck_guest_identities_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)"));
        builder.HasKey(identity => identity.Id);
        builder.Property(identity => identity.Id).HasConversion(new TypedIdConverter<GuestIdentityId>()).ValueGeneratedNever();
        builder.Property(identity => identity.SessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(identity => identity.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(identity => identity.DisplayName).HasConversion(new TextValueConverter<DisplayName>()).HasMaxLength(DisplayName.MaxLength);
        builder.Property(identity => identity.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(identity => identity.Participant);
    }
}
