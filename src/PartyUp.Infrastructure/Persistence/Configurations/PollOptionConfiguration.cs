using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class PollOptionConfiguration : IEntityTypeConfiguration<PollOption>
{
    public void Configure(EntityTypeBuilder<PollOption> builder)
    {
        builder.ToTable("poll_options");
        builder.HasKey(option => option.Id);
        builder.Property(option => option.Id).HasConversion(new TypedIdConverter<PollOptionId>()).ValueGeneratedNever();
        builder.Property(option => option.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(option => option.StartTime).HasColumnType("timestamp with time zone");
        builder.Property(option => option.SuggestedByUserId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(option => option.SuggestedByGuestIdentityId).HasConversion(new TypedIdConverter<GuestIdentityId>());
        builder.Property(option => option.CreatedAt).HasColumnType("timestamp with time zone");

        builder.Ignore(option => option.SuggestedBy);

        // D-080: identical instants merge rather than duplicate — a plain (non-partial) unique
        // index, since PollId and StartTime are both always set (the D-080 merge probe under lock).
        builder.HasIndex(option => new { option.PollId, option.StartTime }).IsUnique().HasDatabaseName("ux_poll_options");
    }
}
