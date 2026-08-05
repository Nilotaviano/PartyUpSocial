using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SetSessionReminderTests
{
    [Fact]
    public async Task RejectsGuestRemovedAndTerminalRowsAndTriggersReplan_D082()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SetSessionReminderUseCase useCase = fixture.ReminderUseCase();

        // A registered, active invitee can set the override, which triggers a replan.
        SetSessionReminderResult ok = await useCase.ExecuteAsync(
            session.Id, fixture.InviteeId, ReminderOffset.Min30, default);
        Assert.True(ok.Succeeded);
        Assert.Contains((session.Id, fixture.InviteeId), fixture.Reminders.Replans);
        Assert.Equal(ReminderOffset.Min30, session.Find(ParticipantRef.ForUser(fixture.InviteeId))!.ReminderChoice);

        // A guest (no participation at all under this user id) is rejected.
        SetSessionReminderResult guestResult = await useCase.ExecuteAsync(
            session.Id, UserId.New(), ReminderOffset.Hours1, default);
        Assert.False(guestResult.Succeeded);
        Assert.Equal("reminder_not_eligible", guestResult.Code);

        // A Removed row is rejected.
        session.RemoveParticipation(ParticipantRef.ForUser(fixture.InviteeId), fixture.Clock);
        _ = session.DequeueEvents();
        SetSessionReminderResult removedResult = await useCase.ExecuteAsync(
            session.Id, fixture.InviteeId, ReminderOffset.Hours1, default);
        Assert.False(removedResult.Succeeded);
        Assert.Equal("reminder_not_eligible", removedResult.Code);

        // A terminal session is rejected outright.
        Session cancelable = fixture.SeedSession(minAttendance: 1, maxCapacity: null, invitees: [fixture.InviteeId]);
        cancelable.Cancel(fixture.Clock);
        _ = cancelable.DequeueEvents();
        SetSessionReminderResult terminalResult = await useCase.ExecuteAsync(
            cancelable.Id, fixture.OrganizerId, ReminderOffset.Hours1, default);
        Assert.False(terminalResult.Succeeded);
        Assert.Equal("reminder_not_eligible", terminalResult.Code);
    }
}
