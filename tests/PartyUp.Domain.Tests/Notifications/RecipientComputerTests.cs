using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Domain.Tests.Notifications;

public class RecipientComputerTests
{
    [Fact]
    public void ForFriendGroupEvent_SendsInviteToTheInvitee_D207()
    {
        FriendGroupId groupId = FriendGroupId.New();
        UserId owner = UserId.New();
        UserId invitee = UserId.New();

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForFriendGroupEvent(
            new FriendGroupInviteSent(groupId, owner, invitee),
            RecipientFilter.None);

        Assert.Equal([invitee], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.FriendGroupInviteReceived, requests[0].Type);
        Assert.Equal(owner, requests[0].Subject.ActorUserId);
    }

    [Fact]
    public void ForFriendGroupEvent_SendsAcceptanceToTheOwner_D207()
    {
        FriendGroupId groupId = FriendGroupId.New();
        UserId owner = UserId.New();
        UserId invitee = UserId.New();

        IReadOnlyList<NotificationRequest> requests = RecipientComputer.ForFriendGroupEvent(
            new FriendGroupInviteAccepted(groupId, owner, invitee),
            RecipientFilter.None);

        Assert.Equal([owner], TestNotifications.Recipients(requests));
        Assert.Equal(NotificationType.FriendGroupInviteAccepted, requests[0].Type);
        Assert.Equal(invitee, requests[0].Subject.ActorUserId);
    }
}
