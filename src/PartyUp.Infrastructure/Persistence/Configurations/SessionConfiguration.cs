using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        builder.ToTable("sessions", table =>
        {
            table.HasCheckConstraint("ck_sessions_duration", "duration_minutes BETWEEN 15 AND 1440 AND duration_minutes % 15 = 0");
            table.HasCheckConstraint("ck_sessions_min_attendance", "min_attendance >= 1");
            table.HasCheckConstraint("ck_sessions_capacity", "max_capacity IS NULL OR max_capacity >= min_attendance");
            table.HasCheckConstraint("ck_sessions_game_shape", "(game_steam_app_id IS NULL OR platform = 0) AND (game_steam_app_id IS NULL OR game_name IS NOT NULL)");
        });
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).HasConversion(new TypedIdConverter<SessionId>()).ValueGeneratedNever();
        builder.Property(session => session.OrganizerId).HasConversion(new TypedIdConverter<UserId>());
        builder.Property(session => session.Title).HasConversion(new TextValueConverter<SessionTitle>()).HasMaxLength(SessionTitle.MaxLength);
        builder.Property(session => session.StartTime).HasColumnType("timestamp with time zone");
        builder.Property(session => session.Duration).HasConversion(duration => duration.TotalMinutes, minutes => Duration15.FromMinutes(minutes)).HasColumnName("duration_minutes");
        builder.Property(session => session.Platform).HasConversion<short>().HasColumnType("smallint");
        builder.Property(session => session.Notes).HasConversion(new TextValueConverter<SessionNotes>()).HasMaxLength(SessionNotes.MaxLength);
        builder.Property(session => session.OriginPollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(session => session.Status).HasConversion<short>().HasColumnType("smallint");
        builder.Property(session => session.CreatedAt).HasColumnType("timestamp with time zone");
        builder.OwnsOne(session => session.Game, game =>
        {
            game.Property(value => value!.Name).HasConversion(new TextValueConverter<GameName>()).HasColumnName("game_name").HasMaxLength(GameName.MaxLength);
            game.Property(value => value!.SteamAppId).HasColumnName("game_steam_app_id");
        });
        builder.Navigation(session => session.Game).IsRequired(false);
        builder.Ignore(session => session.EndTime);
        builder.Ignore(session => session.Window);
        builder.Ignore(session => session.IsTerminal);
        builder.Ignore(session => session.IsRetainedInHistory);
        builder.Ignore(session => session.ActiveGoingCount);
        builder.Ignore(session => session.ActiveOccupancy);
        builder.Ignore(session => session.WaitlistCount);
        builder.Ignore(session => session.HasGoingWaitlistedCandidate);
        builder.Ignore(session => session.HasFreeSlot);
        builder.HasMany(session => session.Participations).WithOne().HasForeignKey(participation => participation.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(session => session.JoinRequests).WithOne().HasForeignKey(request => request.SessionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(session => session.GuestIdentities).WithOne().HasForeignKey(identity => identity.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(session => session.GuestLinks).WithOne().HasForeignKey(link => link.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(session => session.Participations)
            .HasField("_participations")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(session => session.JoinRequests)
            .HasField("_joinRequests")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(session => session.GuestIdentities)
            .HasField("_guestIdentities")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(session => session.GuestLinks)
            .HasField("_guestLinks")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasIndex(session => new { session.OrganizerId, session.Status, session.StartTime }).HasDatabaseName("ix_sessions_organizer_status_start");
        builder.HasIndex(session => new { session.Status, session.StartTime, session.Id }).HasDatabaseName("ix_sessions_status_start_id");
        // PHASE7_DESIGN.md §1: what makes §3's mute join survive finalization (mute.session_id IN
        // (SELECT id FROM sessions WHERE origin_poll_id = n.poll_id)) an index lookup, not a scan.
        builder.HasIndex(session => session.OriginPollId).HasFilter("origin_poll_id IS NOT NULL").HasDatabaseName("ix_sessions_origin_poll_id");
        builder.HasOne<User>().WithMany().HasForeignKey(session => session.OrganizerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Poll>().WithMany().HasForeignKey(session => session.OriginPollId).OnDelete(DeleteBehavior.Restrict);
        builder.Property<uint>("xmin").IsRowVersion();
    }
}
