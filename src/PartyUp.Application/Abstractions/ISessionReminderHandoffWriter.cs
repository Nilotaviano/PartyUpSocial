using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Abstractions;

/// <summary>
/// Plans and cancels the durable §7.1 reminder handoff — the crash boundary a Hangfire enqueue never
/// is. Declared in S1 alongside the other Application ports; implemented and registered by S2
/// (PHASE4_DESIGN.md §5.1, §7.1). Not a substitute for the existing <see cref="IReminderScheduleWriter"/>
/// port, which S2's <c>SessionReminderScheduleWriter</c> takes over from the Phase 3 placeholder.
/// </summary>
public interface ISessionReminderHandoffWriter
{
    /// <summary>
    /// Recomputes and writes the handoff due at <paramref name="recipientUserId"/>'s effective offset
    /// from <paramref name="session"/>'s start, or cancels it when no reminder is currently due
    /// (§7.1: <c>Off</c>, <c>Removed</c>, <c>Declined</c>, a terminal session, or a non-future instant).
    /// </summary>
    Task ReplanAsync(Session session, UserId recipientUserId, CancellationToken cancellationToken);

    /// <summary>Cancels any live handoff for this (session, recipient) pair, incrementing its version.</summary>
    Task CancelAsync(SessionId sessionId, UserId recipientUserId, CancellationToken cancellationToken);
}
