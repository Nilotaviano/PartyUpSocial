using System.Reflection;
using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Domain.Tests.Notifications;

public class NotificationRecordTests
{
    private static readonly Instant Origin = Instant.FromUtc(2026, 3, 2, 12, 0);

    private static FakeClock Clock() => new(Origin);

    // ---- §9: structured refs only ----------------------------------------------------------------

    [Fact]
    public void Notification_CarriesNoTextPayloadAtAll_D112()
    {
        PropertyInfo[] properties = typeof(Notification).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(string));
        Assert.DoesNotContain(
            typeof(Notification).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            field => field.FieldType == typeof(string));
    }

    [Fact]
    public void NotificationSubject_CarriesNoTextPayloadAtAll_D112()
    {
        PropertyInfo[] properties = typeof(NotificationSubject)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.NotEmpty(properties);
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(string));
    }

    [Fact]
    public void Create_StampsTheClockAndStartsUnread_D112()
    {
        FakeClock clock = Clock();
        SessionId sessionId = SessionId.New();
        UserId recipient = UserId.New();

        Notification notification = Notification.Create(
            recipient,
            NotificationType.SessionConfirmed,
            NotificationSubject.ForSession(sessionId),
            clock);

        Assert.Equal(recipient, notification.RecipientUserId);
        Assert.Equal(sessionId, notification.SessionId);
        Assert.Null(notification.PollId);
        Assert.Equal(Origin, notification.CreatedAt);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public void Create_KeepsTheOptionInstantAsAStructuredRef_D120()
    {
        FakeClock clock = Clock();
        Instant suggested = Origin + Duration.FromDays(3);

        Notification notification = Notification.Create(
            UserId.New(),
            NotificationType.PollOptionSuggested,
            NotificationSubject.ForPollOption(PollId.New(), suggested, UserId.New()),
            clock);

        Assert.Equal(suggested, notification.OptionStartTime);
    }

    [Fact]
    public void MarkRead_KeepsTheFirstInstant_D112()
    {
        FakeClock clock = Clock();
        Notification notification = Notification.Create(
            UserId.New(),
            NotificationType.SessionConfirmed,
            NotificationSubject.ForSession(SessionId.New()),
            clock);

        notification.MarkRead(clock);
        clock.Advance(Duration.FromHours(2));
        notification.MarkRead(clock);

        Assert.True(notification.IsRead);
        Assert.Equal(Origin, notification.ReadAt);
    }

    [Fact]
    public void From_MaterializesAComputedRequest_D112()
    {
        FakeClock clock = Clock();
        NotificationRequest request = new(
            UserId.New(),
            NotificationType.WaitlistRejected,
            NotificationSubject.ForSession(SessionId.New()));

        Notification notification = Notification.From(request, clock);

        Assert.Equal(request.RecipientUserId, notification.RecipientUserId);
        Assert.Equal(request.Type, notification.Type);
        Assert.Equal(request.Subject, notification.Subject);
    }

    // ---- shape validation ------------------------------------------------------------------------

    [Fact]
    public void Create_RejectsASessionRowWithoutItsSession_D112()
    {
        FakeClock clock = Clock();

        DomainException error = Assert.Throws<DomainException>(() => Notification.Create(
            UserId.New(),
            NotificationType.SessionCanceled,
            NotificationSubject.ForPoll(PollId.New()),
            clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void Create_RejectsAPollRowWithoutItsPoll_D112()
    {
        FakeClock clock = Clock();

        Assert.Throws<DomainException>(() => Notification.Create(
            UserId.New(),
            NotificationType.PollClosed,
            NotificationSubject.ForSession(SessionId.New()),
            clock));
    }

    [Fact]
    public void Create_RequiresOptionSelectedToNameBothThePollAndTheSession_D112()
    {
        FakeClock clock = Clock();

        Assert.Throws<DomainException>(() => Notification.Create(
            UserId.New(),
            NotificationType.PollOptionSelected,
            NotificationSubject.ForPoll(PollId.New()),
            clock));
    }

    [Fact]
    public void Create_RejectsAFriendRowWithoutItsActor_D105()
    {
        FakeClock clock = Clock();

        Assert.Throws<DomainException>(() => Notification.Create(
            UserId.New(),
            NotificationType.FriendRequestReceived,
            NotificationSubject.Create(null, null),
            clock));
    }

    [Fact]
    public void Create_RejectsAFriendRowCarryingASessionRef_D105()
    {
        FakeClock clock = Clock();

        Assert.Throws<DomainException>(() => Notification.Create(
            UserId.New(),
            NotificationType.SteamImportMatch,
            NotificationSubject.Create(SessionId.New(), null, UserId.New()),
            clock));
    }

    [Fact]
    public void Create_RejectsAnUndefinedType()
    {
        FakeClock clock = Clock();

        DomainException error = Assert.Throws<DomainException>(() => Notification.Create(
            UserId.New(),
            (NotificationType)999,
            NotificationSubject.ForSession(SessionId.New()),
            clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void NotificationType_HasStablePersistedValuesForEverySpecRow_D112()
    {
        NotificationType[] types = Enum.GetValues<NotificationType>();

        // One member per spec §8.4 row plus the reminder, plus the D-207 friend-group trio; renumbering
        // would rewrite stored history.
        Assert.Equal(28, types.Length);
        Assert.Equal(0, (int)NotificationType.SessionInvitation);
        Assert.Equal(27, (int)NotificationType.AudienceGroupDeleted);
        Assert.All(
            types,
            type => Assert.True(
                type.NeedsSessionRef() ||
                type.NeedsPollRef() ||
                type.NeedsActorRef() ||
                // D-205: an AudienceGroupDeleted row carries no ref at all -- the group that would be
                // named is the very thing that is gone, and it is one row per organizer, not per
                // affected session/poll.
                type == NotificationType.AudienceGroupDeleted));
    }

    // ---- NotificationMute ------------------------------------------------------------------------

    [Fact]
    public void Mute_TargetsASessionXorAPoll_D090()
    {
        FakeClock clock = Clock();
        SessionId sessionId = SessionId.New();
        PollId pollId = PollId.New();
        UserId user = UserId.New();

        NotificationMute sessionMute = NotificationMute.ForSession(user, sessionId, clock);
        NotificationMute pollMute = NotificationMute.ForPoll(user, pollId, clock);

        Assert.True(sessionMute.TargetsSession);
        Assert.False(sessionMute.TargetsPoll);
        Assert.True(sessionMute.Targets(sessionId));
        Assert.True(pollMute.TargetsPoll);
        Assert.False(pollMute.TargetsSession);
        Assert.True(pollMute.Targets(pollId));
    }

    [Fact]
    public void AttachToSession_CarriesAPollMuteOntoTheFinalizedSession_D113()
    {
        FakeClock clock = Clock();
        SessionId sessionId = SessionId.New();
        NotificationMute mute = NotificationMute.ForPoll(UserId.New(), PollId.New(), clock);

        mute.AttachToSession(sessionId);

        Assert.True(mute.Targets(sessionId));
        Assert.Null(mute.PollId);
    }

    [Fact]
    public void AttachToSession_RefusesAMuteThatIsAlreadyASessionMute_D113()
    {
        FakeClock clock = Clock();
        NotificationMute mute = NotificationMute.ForSession(UserId.New(), SessionId.New(), clock);

        DomainException error = Assert.Throws<DomainException>(() => mute.AttachToSession(SessionId.New()));

        Assert.Equal(DomainError.InvalidTransition, error.Error);
    }

    // ---- MuteSet ---------------------------------------------------------------------------------

    [Fact]
    public void MuteSet_MatchesOnlyTheMutedPair_D090()
    {
        FakeClock clock = Clock();
        UserId muter = UserId.New();
        UserId other = UserId.New();
        SessionId sessionId = SessionId.New();

        MuteSet mutes = MuteSet.Create([NotificationMute.ForSession(muter, sessionId, clock)]);

        Assert.True(mutes.IsMuted(muter, sessionId));
        Assert.False(mutes.IsMuted(other, sessionId));
        Assert.False(mutes.IsMuted(muter, SessionId.New()));
        Assert.Equal(1, mutes.Count);
    }

    [Fact]
    public void MuteSet_TreatsEitherReferencedTargetAsMuting_D113()
    {
        FakeClock clock = Clock();
        UserId user = UserId.New();
        PollId pollId = PollId.New();
        SessionId sessionId = SessionId.New();

        MuteSet mutes = MuteSet.Create([NotificationMute.ForPoll(user, pollId, clock)]);

        // The finalization rows name the poll and the session it minted at once (§12-15).
        Assert.True(mutes.IsMuted(user, sessionId, pollId));
        Assert.False(mutes.IsMuted(user, sessionId, null));
    }

    [Fact]
    public void MuteSet_EmptyMutesNothing_D090()
    {
        Assert.False(MuteSet.Empty.IsMuted(UserId.New(), SessionId.New()));
        Assert.Equal(0, MuteSet.Empty.Count);
        Assert.True(RecipientFilter.None.Allows(UserId.New(), NotificationSubject.ForSession(SessionId.New())));
    }

    // ---- PushSubscription (shape only in Phase 1) -------------------------------------------------

    [Fact]
    public void PushSubscription_CapturesTheEndpointAndKeysAndStartsActive()
    {
        FakeClock clock = Clock();
        UserId user = UserId.New();

        PushSubscription subscription = PushSubscription.Create(
            user,
            "  https://push.example/endpoint/abc  ",
            "p256dh-key",
            "auth-secret",
            clock);

        Assert.Equal(user, subscription.UserId);
        Assert.Equal("https://push.example/endpoint/abc", subscription.Endpoint);
        Assert.Equal("p256dh-key", subscription.PublicKey);
        Assert.Equal("auth-secret", subscription.AuthSecret);
        Assert.True(subscription.IsActive);
        Assert.Equal(Origin, subscription.CreatedAt);
    }

    [Theory]
    [InlineData("", "key", "auth")]
    [InlineData("   ", "key", "auth")]
    [InlineData("https://push.example", "", "auth")]
    [InlineData("https://push.example", "key", "  ")]
    public void PushSubscription_RequiresAnEndpointAndBothKeys(string endpoint, string publicKey, string authSecret)
    {
        FakeClock clock = Clock();

        DomainException error = Assert.Throws<DomainException>(
            () => PushSubscription.Create(UserId.New(), endpoint, publicKey, authSecret, clock));

        Assert.Equal(DomainError.ValidationFailed, error.Error);
    }

    [Fact]
    public void PushSubscription_RejectsAnOverlongEndpoint()
    {
        FakeClock clock = Clock();
        string endpoint = new('e', PushSubscription.MaximumEndpointLength + 1);

        Assert.Throws<DomainException>(
            () => PushSubscription.Create(UserId.New(), endpoint, "key", "auth", clock));
    }

    [Fact]
    public void PushSubscription_RevokesIdempotently()
    {
        FakeClock clock = Clock();
        PushSubscription subscription = PushSubscription.Create(
            UserId.New(), "https://push.example", "key", "auth", clock);

        subscription.Revoke(clock);
        clock.Advance(Duration.FromHours(1));
        subscription.Revoke(clock);

        Assert.False(subscription.IsActive);
        Assert.Equal(Origin, subscription.RevokedAt);
    }
}
