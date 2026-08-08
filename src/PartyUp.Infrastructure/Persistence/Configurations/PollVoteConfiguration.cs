using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

/// <summary>
/// A vote's identity is the (option, participant) pair — <see cref="PollVote"/> mints no id
/// (`PollVote.cs:7-10`). Postgres cannot key a table on columns half of which are always null
/// (only one of user_id/guest_identity_id is ever set), so this shadow surrogate key exists only at
/// the persistence boundary, generated the same A-015 way every other id is (Guid.CreateVersion7).
/// </summary>
public sealed class PollVoteConfiguration : IEntityTypeConfiguration<PollVote>
{
    public void Configure(EntityTypeBuilder<PollVote> builder)
    {
        builder.ToTable("poll_votes", table =>
            table.HasCheckConstraint("ck_poll_votes_participant", "(user_id IS NOT NULL) <> (guest_identity_id IS NOT NULL)"));

        builder.Property<Guid>("Id").HasColumnName("id").HasValueGenerator<GuidVersion7ValueGenerator>().ValueGeneratedOnAdd();
        builder.HasKey("Id");

        builder.Property(vote => vote.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(vote => vote.OptionId).HasConversion(new TypedIdConverter<PollOptionId>());
        builder.Property(vote => vote.UserId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(vote => vote.GuestIdentityId).HasConversion(new TypedIdConverter<GuestIdentityId>());
        builder.Property(vote => vote.Choice).HasConversion<short>().HasColumnType("smallint");
        builder.Property(vote => vote.SubmittedAt).HasColumnType("timestamp with time zone");
        builder.Property(vote => vote.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.Ignore(vote => vote.Participant);

        builder.HasIndex(vote => new { vote.OptionId, vote.UserId }).IsUnique().HasFilter("user_id IS NOT NULL").HasDatabaseName("ux_poll_votes_option_user");
        builder.HasIndex(vote => new { vote.OptionId, vote.GuestIdentityId }).IsUnique().HasFilter("guest_identity_id IS NOT NULL").HasDatabaseName("ux_poll_votes_option_guest");

        builder.HasOne<PollOption>().WithMany().HasForeignKey(vote => vote.OptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(vote => vote.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<GuestIdentity>().WithMany().HasForeignKey(vote => vote.GuestIdentityId).OnDelete(DeleteBehavior.Restrict);
    }

    private sealed class GuidVersion7ValueGenerator : ValueGenerator<Guid>
    {
        public override bool GeneratesTemporaryValues => false;

        public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();
    }
}
