using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Domain.Tests.Notifications;

internal static class TestNotifications
{
    internal static RecipientFilter MutedOn(IClock clock, UserId user, SessionId sessionId) =>
        RecipientFilter.Create(MuteSet.Create([NotificationMute.ForSession(user, sessionId, clock)]));

    internal static RecipientFilter MutedOn(IClock clock, UserId user, PollId pollId) =>
        RecipientFilter.Create(MuteSet.Create([NotificationMute.ForPoll(user, pollId, clock)]));

    internal static IReadOnlyList<UserId> Recipients(IEnumerable<NotificationRequest> requests) =>
        [.. requests.Select(request => request.RecipientUserId)];

    /// <summary>The single event of the given type on a drained aggregate.</summary>
    internal static TEvent Single<TEvent>(IEnumerable<IDomainEvent> events)
        where TEvent : IDomainEvent =>
        events.OfType<TEvent>().Single();

    internal static IReadOnlyList<TEvent> All<TEvent>(IEnumerable<IDomainEvent> events)
        where TEvent : IDomainEvent =>
        [.. events.OfType<TEvent>()];
}
