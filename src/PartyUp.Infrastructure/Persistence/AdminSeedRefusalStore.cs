using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence;

public sealed class AdminSeedRefusalStore(AppDbContext context) : IAdminSeedRefusalStore
{
    public Task<bool> ExistsAsync(
        UserId userId,
        CancellationToken cancellationToken) =>
        context.AdminSeedRefusals
            .AsNoTracking()
            .AnyAsync(refusal => refusal.UserId == userId.Value, cancellationToken);

    public async Task RecordAsync(
        UserId userId,
        Instant refusedAt,
        CancellationToken cancellationToken) =>
        _ = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO admin_seed_refusals (user_id, refused_at)
            VALUES ({userId.Value}, {refusedAt})
            ON CONFLICT (user_id) DO NOTHING
            """,
            cancellationToken);
}

public sealed class AdminSeedRefusal
{
    private AdminSeedRefusal()
    {
    }

    public AdminSeedRefusal(Guid userId, Instant refusedAt)
    {
        UserId = userId;
        RefusedAt = refusedAt;
    }

    public Guid UserId { get; private set; }

    public Instant RefusedAt { get; private set; }
}
