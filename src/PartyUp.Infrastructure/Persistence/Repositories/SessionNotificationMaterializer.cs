using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The real <see cref="ISessionNotificationMaterializer"/> (PHASE4_DESIGN.md §5.1, §6.3), replacing the
/// Phase 4 <c>NoOpSessionNotificationMaterializer</c> DI placeholder. It accepts structured Session
/// events only and routes every one of them through <see cref="RecipientComputer.ForSessionEvent"/> —
/// the pure §10 recipient contract already proven by <c>PartyUp.Domain.Tests</c> — so this adapter adds
/// no recipient logic of its own: it supplies the write-time mute lookup, then persists whatever
/// <see cref="NotificationRequest"/>s come back. Guests are never recipients (D-167) because a guest
/// <see cref="Domain.Kernel.ParticipantRef"/> carries no <see cref="UserId"/> for
/// <c>RecipientComputer</c>'s explicit-target rows to resolve. Block- and deletion-driven session
/// events carry no block cause, direction, or actor (D-105) because the domain events themselves never
/// reference one — <see cref="SessionCanceled"/> is the only session event with an actor ref, and it
/// names the canceling organizer, not a blocker.
/// </summary>
public sealed class SessionNotificationMaterializer(AppDbContext context, IClock clock)
    : ISessionNotificationMaterializer
{
    public async Task MaterializeAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return;
        }

        List<NotificationMute> mutes = await context.NotificationMutes
            .Where(mute => mute.SessionId == session.Id)
            .ToListAsync(cancellationToken);

        RecipientFilter filter = RecipientFilter.Create(MuteSet.Create(mutes));

        foreach (IDomainEvent domainEvent in events)
        {
            foreach (NotificationRequest request in RecipientComputer.ForSessionEvent(session, domainEvent, filter))
            {
                context.Notifications.Add(Notification.From(request, clock));
            }
        }
    }
}
