using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Domain.Tests.Notifications;

public class FriendRecipientTests
{
    [Fact]
    public void FriendRequest_ReachesTheAddresseeOnly_D105()
    {
        UserId requester = UserId.New();
        UserId addressee = UserId.New();

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.FriendRequest(
            new FriendRequestReceived(FriendshipId.New(), requester, addressee),
            RecipientFilter.None);

        Assert.Equal([addressee], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.FriendRequestReceived, requests[0].Type);
        Assert.Equal(requester, requests[0].Subject.ActorUserId);
        Assert.Null(requests[0].Subject.SessionId);
        Assert.Null(requests[0].Subject.PollId);
    }

    [Fact]
    public void ForFriendshipEvent_KeepsAcceptanceAndBlockingSilent_D105()
    {
        UserId first = UserId.New();
        UserId second = UserId.New();

        Assert.Empty(RecipientComputer.ForFriendshipEvent(
            new FriendshipAccepted(FriendshipId.New(), first, second), RecipientFilter.None));
        Assert.Empty(RecipientComputer.ForFriendshipEvent(
            new UserBlocked(BlockId.New(), first, second), RecipientFilter.None));
    }

    [Fact]
    public void ForFriendshipEvent_RoutesTheReceivedRequest_D105()
    {
        UserId requester = UserId.New();
        UserId addressee = UserId.New();

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForFriendshipEvent(
            new FriendRequestReceived(FriendshipId.New(), requester, addressee),
            RecipientFilter.None);

        Assert.Equal([addressee], TestNotifications.Recipients(requests));
    }

    [Fact]
    public void SteamImportMatch_ReachesTheImporterAndNeverTheMatchedUser_D105()
    {
        UserId importer = UserId.New();
        UserId matched = UserId.New();

        IReadOnlyList<NotificationRequest> requests =
            RecipientComputer.SteamImportMatch(importer, matched, RecipientFilter.None);

        // §12-32: notifying the matched side would disclose the importer's Steam friend list before
        // any friend request exists.
        Assert.Equal([importer], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.SteamImportMatch, requests[0].Type);
        Assert.Equal(matched, requests[0].Subject.ActorUserId);
    }

    [Fact]
    public void SteamImportMatch_IsSuppressedForTheUserWhoseDeletionIsExecuting_D092()
    {
        UserId importer = UserId.New();
        RecipientFilter filter = RecipientFilter.None.Excluding(importer);

        Assert.Empty(RecipientComputer.SteamImportMatch(importer, UserId.New(), filter));
    }

    [Fact]
    public void SteamImportMatch_MaterializesAsAStructuredRow_D105()
    {
        FakeClock clock = new(Instant.FromUtc(2026, 3, 2, 12, 0));
        UserId importer = UserId.New();
        UserId matched = UserId.New();

        Notification notification = Notification.From(
            RecipientComputer.SteamImportMatch(importer, matched, RecipientFilter.None)[0],
            clock);

        Assert.Equal(importer, notification.RecipientUserId);
        Assert.Equal(matched, notification.ActorUserId);
        Assert.Null(notification.SessionId);
    }
}
