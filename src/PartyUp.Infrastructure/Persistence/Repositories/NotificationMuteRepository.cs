using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class NotificationMuteRepository(AppDbContext context) : INotificationMuteRepository
{
    public Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        context.NotificationMutes
            .Where(mute => mute.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

    // ARCHITECTURE §3: logical uniqueness is a DB constraint, not an app check — the two partial
    // unique indexes (§1) are the whole idempotency mechanism, reached only through
    // INSERT ... ON CONFLICT DO NOTHING, on the shipped NotificationRepository.AddSteamMatchIfAbsentAsync
    // idiom.
    public Task AddAsync(NotificationMute mute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mute);

        return mute.TargetsSession
            ? context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_mutes (id, user_id, session_id, poll_id, created_at)
                VALUES ({mute.Id.Value}, {mute.UserId.Value}, {mute.SessionId!.Value.Value}, NULL, {mute.CreatedAt})
                ON CONFLICT (session_id, user_id) WHERE session_id IS NOT NULL DO NOTHING
                """, cancellationToken)
            : context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_mutes (id, user_id, session_id, poll_id, created_at)
                VALUES ({mute.Id.Value}, {mute.UserId.Value}, NULL, {mute.PollId!.Value.Value}, {mute.CreatedAt})
                ON CONFLICT (poll_id, user_id) WHERE poll_id IS NOT NULL DO NOTHING
                """, cancellationToken);
    }

    public Task RemoveForTargetAsync(
        UserId userId,
        SessionId? sessionId,
        PollId? pollId,
        CancellationToken cancellationToken) =>
        context.NotificationMutes
            .Where(mute => mute.UserId == userId && mute.SessionId == sessionId && mute.PollId == pollId)
            .ExecuteDeleteAsync(cancellationToken);

    public Task<bool> ExistsForTargetAsync(
        UserId userId,
        SessionId? sessionId,
        PollId? pollId,
        CancellationToken cancellationToken) =>
        context.NotificationMutes
            .AsNoTracking()
            .AnyAsync(
                mute => mute.UserId == userId && mute.SessionId == sessionId && mute.PollId == pollId,
                cancellationToken);
}
