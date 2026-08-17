using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using NodaTime;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class PollConfiguration : IEntityTypeConfiguration<Poll>
{
    public void Configure(EntityTypeBuilder<Poll> builder)
    {
        builder.ToTable("polls", table =>
        {
            table.HasCheckConstraint("ck_polls_min_attendance", "min_attendance >= 1");
            table.HasCheckConstraint("ck_polls_capacity", "max_capacity IS NULL OR max_capacity >= min_attendance");
            table.HasCheckConstraint("ck_polls_game_shape", "(game_steam_app_id IS NULL OR platform = 0) AND (game_steam_app_id IS NULL OR game_name IS NOT NULL)");
            // The engine fails closed on an unknown level; the column must never hold one.
            table.HasCheckConstraint("ck_polls_visibility", "visibility IN (0, 1, 2)");
        });
        builder.HasKey(poll => poll.Id);
        builder.Property(poll => poll.Id).HasConversion(new TypedIdConverter<PollId>()).ValueGeneratedNever();
        builder.Property(poll => poll.OrganizerId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(poll => poll.Title).HasConversion(new TextValueConverter<SessionTitle>()).HasMaxLength(SessionTitle.MaxLength);
        builder.Property(poll => poll.Platform).HasConversion<short>().HasColumnType("smallint");
        builder.Property(poll => poll.Notes).HasConversion(new TextValueConverter<SessionNotes>()).HasMaxLength(SessionNotes.MaxLength);
        builder.Property(poll => poll.DeadlineChoice).HasConversion<short>().HasColumnType("smallint");
        builder.Property(poll => poll.Deadline).HasColumnType("timestamp with time zone");
        builder.Property(poll => poll.Status).HasConversion<short>().HasColumnType("smallint");
        builder.Property(poll => poll.Visibility).HasConversion<short>().HasColumnType("smallint");
        builder.Property(poll => poll.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(poll => poll.SelectedOptionId).HasConversion(new TypedIdConverter<PollOptionId>());
        builder.Property(poll => poll.CreatedSessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.OwnsOne(poll => poll.Game, game =>
        {
            game.Property(value => value!.Name).HasConversion(new TextValueConverter<GameName>()).HasColumnName("game_name").HasMaxLength(GameName.MaxLength);
            game.Property(value => value!.SteamAppId).HasColumnName("game_steam_app_id");
        });
        builder.Navigation(poll => poll.Game).IsRequired(false);
        builder.OwnsMany<PollAudienceGroup>("_audienceGroups", audience =>
        {
            audience.ToTable("poll_audience_groups");
            audience.WithOwner().HasForeignKey("PollId");
            audience.Property<PollId>("PollId").HasConversion(new TypedIdConverter<PollId>());
            audience.Property(row => row.GroupId).HasConversion(new TypedIdConverter<FriendGroupId>());
            audience.HasKey("PollId", nameof(PollAudienceGroup.GroupId));
            // D-205: hard-deleting a group empties every audience that targeted it.
            audience.HasOne<FriendGroup>()
                .WithMany()
                .HasForeignKey(row => row.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // D-088/§1: the 48-hour selection-window end is a pure function of Deadline, but the
        // materialized-status invariant forbids deriving it in a query — this shadow column is
        // written once, at insert, by the value generator below rather than by SQL.
        builder.Property<Instant>("SelectionWindowEnd")
            .HasColumnName("selection_window_end")
            .HasColumnType("timestamp with time zone")
            .HasValueGenerator<SelectionWindowEndGenerator>()
            .ValueGeneratedOnAdd();

        builder.Ignore(poll => poll.Details);
        builder.Ignore(poll => poll.SelectionWindowEndsAt);
        builder.Ignore(poll => poll.IsTerminal);
        builder.Ignore(poll => poll.IsRetainedInHistory);
        builder.Ignore(poll => poll.ActiveParticipantCount);

        builder.HasMany(poll => poll.Options).WithOne().HasForeignKey(option => option.PollId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(poll => poll.Participants).WithOne().HasForeignKey(participant => participant.PollId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(poll => poll.Votes).WithOne().HasForeignKey(vote => vote.PollId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(poll => poll.JoinRequests).WithOne().HasForeignKey(request => request.PollId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(poll => poll.GuestIdentities).WithOne().HasForeignKey(identity => identity.PollId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(poll => poll.GuestLinks).WithOne().HasForeignKey(link => link.PollId).OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(poll => poll.Options).HasField("_options").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(poll => poll.Participants).HasField("_participants").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(poll => poll.Votes).HasField("_votes").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(poll => poll.JoinRequests).HasField("_joinRequests").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(poll => poll.GuestIdentities).HasField("_guestIdentities").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(poll => poll.GuestLinks).HasField("_guestLinks").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(poll => new { poll.OrganizerId, poll.Status, poll.Deadline }).HasDatabaseName("ix_polls_organizer_status_deadline");
        builder.HasIndex(poll => new { poll.Status, poll.Deadline, poll.Id }).HasDatabaseName("ix_polls_status_deadline_id");

        builder.HasOne<User>().WithMany().HasForeignKey(poll => poll.OrganizerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PollOption>().WithMany().HasForeignKey(poll => poll.SelectedOptionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Session>().WithMany().HasForeignKey(poll => poll.CreatedSessionId).OnDelete(DeleteBehavior.Restrict);
    }

    /// <summary>
    /// Mapping-level, not SQL: computed once from the tracked <see cref="Poll.Deadline"/> value at
    /// insert time (Deadline is immutable after creation, §12-26), so no recompute-on-update path exists.
    /// </summary>
    private sealed class SelectionWindowEndGenerator : ValueGenerator<Instant>
    {
        public override bool GeneratesTemporaryValues => false;

        public override Instant Next(EntityEntry entry)
        {
            var deadline = (Instant)entry.Property(nameof(Poll.Deadline)).CurrentValue!;
            return PollSelectionWindow.EndsAt(deadline);
        }
    }
}
