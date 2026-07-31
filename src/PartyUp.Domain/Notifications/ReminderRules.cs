using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Domain.Notifications;

/// <summary>
/// The reminder rules of DOMAIN_MODEL §9, as the pure functions the Phase 4 scheduling job calls.
/// A reminder targets Going/Maybe <em>registered</em> participants — waitlisted included, because
/// D-090's exception reads "Going or Maybe users" with no waitlist qualifier and D-084 makes that
/// selected status real (§12-30) — fires <c>ReminderChoice ?? ReminderDefault</c> ahead of the start
/// (D-082, default one hour), and is the sole notification exempt from muting (D-090/D-113).
/// </summary>
/// <remarks>
/// Recompute triggers are every command that can move an input: RSVP, waitlist flag changes
/// (promotion, reject), start-time and duration changes, cancellation, and block- or deletion-driven
/// removal — a <c>Removed</c> participant's pending reminder must die with their access.
/// <see cref="Plan"/> returns <c>null</c> in each of those cases, which is what
/// <see cref="ShouldReschedule(Instant?, ReminderPlan?)"/> reads as "cancel".
/// </remarks>
public static class ReminderRules
{
    /// <summary>
    /// §9/§12-30: registered, not removed, and Going or Maybe — waitlisted rows included. Pending
    /// invitees and Declined participants get nothing.
    /// </summary>
    public static bool IsEligible(Participation participation)
    {
        ArgumentNullException.ThrowIfNull(participation);

        return participation.IsRegistered
            && !participation.IsRemoved
            && participation.Status is ParticipationStatus.Going or ParticipationStatus.Maybe;
    }

    /// <summary>D-082: the per-session override wins; otherwise the account default (itself 1 h by default).</summary>
    public static ReminderOffset EffectiveOffset(ReminderOffset? choice, ReminderOffset accountDefault) =>
        choice ?? accountDefault;

    /// <summary>
    /// The instant the reminder fires, or <c>null</c> when the effective offset is <c>Off</c>. Pure
    /// arithmetic over the start time — no clock, so the caller decides what "already past" means.
    /// </summary>
    public static Instant? ComputeReminderInstant(
        Instant startTime,
        ReminderOffset? choice,
        ReminderOffset accountDefault) =>
        EffectiveOffset(choice, accountDefault).ReminderInstantFor(startTime);

    /// <summary>
    /// The reminder one participant should currently hold, or <c>null</c> when they should hold none:
    /// the session is terminal, they are not on the roster, they are not Going/Maybe, their effective
    /// offset is <c>Off</c>, the computed instant is already past (§9 — never fired late, which
    /// D-153 finalization makes routine), or the deletion executor excluded them (§11).
    /// </summary>
    public static ReminderPlan? Plan(
        Session session,
        UserId user,
        ReminderOffset accountDefault,
        IClock clock,
        RecipientFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);

        if (!(filter ?? RecipientFilter.None).AllowsReminder(user))
        {
            return null;
        }

        if (session.IsTerminal)
        {
            return null;
        }

        if (session.Find(ParticipantRef.ForUser(user)) is not { } row || !IsEligible(row))
        {
            return null;
        }

        if (ComputeReminderInstant(session.StartTime, row.ReminderChoice, accountDefault) is not { } fireAt)
        {
            return null;
        }

        return fireAt < clock.GetCurrentInstant() ? null : new ReminderPlan(session.Id, user, fireAt);
    }

    /// <summary>
    /// Every reminder a session should currently hold — what §6.4-9 schedules for a freshly finalized
    /// roster and what a reschedule recomputes. <paramref name="accountDefaults"/> supplies each
    /// user's <c>ReminderDefault</c>; the User aggregate lives outside this one, so it is a parameter.
    /// </summary>
    public static IReadOnlyList<ReminderPlan> PlanAll(
        Session session,
        Func<UserId, ReminderOffset> accountDefaults,
        IClock clock,
        RecipientFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(accountDefaults);

        List<ReminderPlan> plans = [];

        foreach (Participation row in session.Participations)
        {
            if (row.UserId is { } userId
                && IsEligible(row)
                && Plan(session, userId, accountDefaults(userId), clock, filter) is { } plan)
            {
                plans.Add(plan);
            }
        }

        return plans;
    }

    /// <summary>
    /// Whether the job must act: the instant it currently holds (<c>null</c> = nothing scheduled) no
    /// longer equals the instant the domain says it should hold (<c>null</c> = cancel).
    /// </summary>
    public static bool ShouldReschedule(Instant? scheduledFor, ReminderPlan? plan) =>
        scheduledFor != plan?.FireAt;

    public static bool ShouldReschedule(
        Instant? scheduledFor,
        Session session,
        UserId user,
        ReminderOffset accountDefault,
        IClock clock,
        RecipientFilter? filter = null) =>
        ShouldReschedule(scheduledFor, Plan(session, user, accountDefault, clock, filter));

    /// <summary>
    /// The record a due plan writes. No mute check: the reminder is D-090's stated exception, and
    /// whether it exists at all was already decided by <see cref="Plan"/>.
    /// </summary>
    public static NotificationRequest ToRequest(ReminderPlan plan) =>
        new(plan.UserId, NotificationType.SessionReminder, NotificationSubject.ForSession(plan.SessionId));
}
