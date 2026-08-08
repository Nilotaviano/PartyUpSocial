using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Polls;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The real <see cref="IPollNotificationMaterializer"/>, mirroring
/// <see cref="SessionNotificationMaterializer"/>: accepts structured Poll events only and routes
/// every one of them through <see cref="RecipientComputer.ForPollEvent"/> — the pure §10 recipient
/// contract already proven by <c>PartyUp.Domain.Tests</c> — so this adapter adds no recipient logic
/// of its own beyond the write-time mute lookup. Guests are never recipients (D-167) because a guest
/// <see cref="Domain.Kernel.ParticipantRef"/> carries no <see cref="UserId"/> for
/// <c>RecipientComputer</c>'s explicit-target rows to resolve.
/// </summary>
public sealed class PollNotificationMaterializer(AppDbContext context, IClock clock)
    : IPollNotificationMaterializer
{
    public Task MaterializeAsync(
        Poll poll,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken) =>
        MaterializeAsync(poll, events, excludeUserId: null, cancellationToken);

    /// <summary>
    /// DOMAIN_MODEL.md §11 step 5: <paramref name="excludeUserId"/> is the deletion subject when this
    /// batch of events is deletion-derived, threaded straight into <see cref="RecipientFilter"/> so
    /// the deleting user never receives a notification about their own deletion's effects.
    /// </summary>
    public async Task MaterializeAsync(
        Poll poll,
        IReadOnlyList<IDomainEvent> events,
        UserId? excludeUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        List<NotificationMute> mutes = await context.NotificationMutes
            .Where(mute => mute.PollId == poll.Id)
            .ToListAsync(cancellationToken);

        RecipientFilter filter = RecipientFilter.Create(MuteSet.Create(mutes), excludeUserId);

        foreach (IDomainEvent domainEvent in events)
        {
            foreach (NotificationRequest request in RecipientComputer.ForPollEvent(poll, domainEvent, filter))
            {
                context.Notifications.Add(Notification.From(request, clock));
            }
        }
    }
}
