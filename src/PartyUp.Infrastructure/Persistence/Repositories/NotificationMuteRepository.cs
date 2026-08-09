using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class NotificationMuteRepository(AppDbContext context) : INotificationMuteRepository
{
    public Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        context.NotificationMutes
            .Where(mute => mute.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
}
