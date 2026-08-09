using Hangfire;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// Ruling 8 (PHASE7_DESIGN.md §3/§9): daily deletion of send-log rows older than 30 days. Past the 24 h
/// lateness ceiling nothing can ever re-dispatch against a row this old, so it serves debugging only.
/// </summary>
public sealed class PushSendLogPruneJob(AppDbContext context, IClock clock)
{
    private static readonly Duration Retention = Duration.FromDays(30);

    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Instant cutoff = clock.GetCurrentInstant() - Retention;
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM notification_push_sends WHERE claimed_at < {cutoff}",
            cancellationToken);
    }
}
