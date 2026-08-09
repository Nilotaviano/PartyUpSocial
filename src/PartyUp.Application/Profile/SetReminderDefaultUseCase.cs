using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

/// <summary>
/// PHASE7_DESIGN.md §4's reminder-default replan (closes a live D-082 gap): writing
/// <c>user.SetReminderDefault</c> alone leaves every existing handoff at the old instant, so a
/// widening change (e.g. 1h → 24h) reads as a now-past instant at fire time and the job silently
/// cancels it. This use case pages the user's future non-terminal sessions where they hold an
/// eligible participation, in pages of 100 until exhausted, taking the shipped lock order per session
/// — <c>LockUsersAsync</c> then <c>LockSessionsAsync</c> (<c>SetSessionReminderUseCase.cs:35-36</c>,
/// A-066) — and calls <see cref="ISessionReminderHandoffWriter.ReplanAsync"/> with the new offset
/// passed explicitly (never read back — the user mutation above is still unsaved inside this
/// delegate).
/// </summary>
public sealed class SetReminderDefaultUseCase(
    IUserRepository users,
    ISessionRepository sessions,
    ISessionLockRepository sessionLocks,
    ISessionReminderHandoffWriter reminders,
    IUnitOfWork unitOfWork,
    IClock clock)
{
    private const int PageSize = 100;

    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        ReminderOffset reminder,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.SetReminderDefault(reminder);

                Instant now = clock.GetCurrentInstant();
                (Instant StartTime, Guid Id)? cursor = null;
                while (true)
                {
                    IReadOnlyList<FutureEligibleSession> page = await sessions
                        .ListFutureSessionsWithEligibleParticipationAsync(userId, now, cursor, PageSize, ct);
                    if (page.Count == 0)
                    {
                        break;
                    }

                    foreach (FutureEligibleSession candidate in page)
                    {
                        await sessionLocks.LockUsersAsync([userId], ct);
                        await sessionLocks.LockSessionsAsync([candidate.Id], ct);

                        SessionForUpdate? forUpdate = await sessions.LoadForUpdateAsync(candidate.Id, ct);
                        if (forUpdate is not null)
                        {
                            await reminders.ReplanAsync(forUpdate.Session, userId, ct, reminder);
                        }
                    }

                    if (page.Count < PageSize)
                    {
                        break;
                    }

                    FutureEligibleSession last = page[^1];
                    cursor = (last.StartTime, last.Id.Value);
                }

                return ProfileMutationResult.Completed;
            },
            cancellationToken);
}
