using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;
using PartyUp.Domain.Tests.Users;
using PartyUp.Domain.Users;

namespace PartyUp.Domain.Tests.Notifications;

/// <summary>
/// D-069's two halves: a per-session mute and a customizable reminder default, the one preference
/// muting never touches (§9).
/// </summary>
public class NotificationPreferenceTests
{
    [Fact]
    public void SessionMute_SuppressesThatSessionsRecordsForThatUserOnly_D069()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 2, minAttendance: 2);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);
        SessionConfirmed confirmed = TestNotifications.Single<SessionConfirmed>(fixture.Drain());

        NotificationMute mute = NotificationMute.ForSession(fixture.Invitee(1), fixture.Session.Id, clock);
        RecipientFilter filter = RecipientFilter.Create(MuteSet.Create([mute]));

        IReadOnlyList<UserId> recipients = TestNotifications.Recipients(
            RecipientComputer.Confirmed(fixture.Session, confirmed, filter));

        Assert.True(mute.TargetsSession);
        Assert.DoesNotContain(fixture.Invitee(1), recipients);
        Assert.Contains(fixture.Invitee(0), recipients);
    }

    [Fact]
    public void ReminderDefault_StartsAtOneHourAndIsCustomizable_D069()
    {
        FakeClock clock = TestUsers.Clock();
        User user = TestUsers.Active(clock);

        Assert.Equal(ReminderOffset.Hours1, user.ReminderDefault);

        user.SetReminderDefault(ReminderOffset.Hours3);

        Assert.Equal(ReminderOffset.Hours3, user.ReminderDefault);
    }

    [Fact]
    public void ReminderChoice_OverridesTheAccountDefaultForOneSession_D069()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest fixture = TestSessions.New(clock, inviteeCount: 1, minAttendance: 1);
        fixture.Session.Rsvp(fixture.InviteeRef(0), ParticipationStatus.Going, clock);

        fixture.Session.SetReminderChoice(fixture.Invitee(0), ReminderOffset.Min15, clock);

        ReminderPlan? plan = ReminderRules.Plan(
            fixture.Session, fixture.Invitee(0), ReminderOffset.Hours24, clock);

        Assert.NotNull(plan);
        Assert.Equal(fixture.Session.StartTime - Duration.FromMinutes(15), plan.Value.FireAt);
    }
}
