using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// One scheduled session reminder (§9): who it is for, which session, and the instant it fires. The
/// Phase 4 job holds nothing else — the record itself is minted at firing time from
/// <see cref="ReminderRules.ToRequest"/>, so a plan never carries text.
/// </summary>
public readonly record struct ReminderPlan(SessionId SessionId, UserId UserId, Instant FireAt);
