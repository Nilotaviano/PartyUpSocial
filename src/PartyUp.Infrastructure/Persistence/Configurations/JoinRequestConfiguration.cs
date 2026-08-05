using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence.Converters;

namespace PartyUp.Infrastructure.Persistence.Configurations;

public sealed class JoinRequestConfiguration : IEntityTypeConfiguration<JoinRequest>
{
    public void Configure(EntityTypeBuilder<JoinRequest> builder)
    {
        builder.ToTable("join_requests", table => table.HasCheckConstraint("ck_join_requests_target", "(session_id IS NOT NULL) <> (poll_id IS NOT NULL)"));
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Id).HasConversion(new TypedIdConverter<JoinRequestId>()).ValueGeneratedNever();
        builder.Property(request => request.SessionId).HasConversion(new TypedIdConverter<SessionId>());
        builder.Property(request => request.PollId).HasConversion(new TypedIdConverter<PollId>());
        builder.Property(request => request.RequesterId).HasConversion(new TypedIdConverter<UserId>()).HasColumnName("requester_user_id");
        builder.Property(request => request.Status).HasConversion<short>().HasColumnType("smallint");
        builder.Property(request => request.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(request => request.ResolvedAt).HasColumnType("timestamp with time zone");
        builder.Ignore(request => request.IsPending);
        builder.HasIndex(request => new { request.SessionId, request.Status, request.CreatedAt, request.Id }).HasDatabaseName("ix_join_requests_session_status_created_id");
        builder.HasIndex(request => new { request.SessionId, request.RequesterId }).IsUnique().HasFilter("session_id IS NOT NULL AND status = 0").HasDatabaseName("ux_join_requests_session_pending_requester");
        builder.HasOne<User>().WithMany().HasForeignKey(request => request.RequesterId).OnDelete(DeleteBehavior.Restrict);
    }
}
