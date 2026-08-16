using NodaTime.Testing;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Tests.Sessions;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

/// <summary>
/// D-007's two halves, which no other test names together: an eligible non-invited friend both sees
/// a non-private session and may request to join it, and sees nothing once it is private.
/// </summary>
public class DiscoveryAccessTests
{
    [Fact]
    public void NonInvitedFriend_SeesTheSessionAndCanRequestToJoin_D007()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        Viewer friend = TestViewers.FriendOf(s.Organizer);

        VisibilityResult result = VisibilityEngine.ResolveSession(s.Session, friend);
        JoinRequest request = s.Session.OpenJoinRequest(friend.Id, clock);

        Assert.Equal(VisibilityGrant.Inherited, result.Grant);
        Assert.Equal(s.Organizer, result.ExtenderId);
        Assert.Equal(JoinRequestStatus.Pending, request.Status);
    }

    [Fact]
    public void NonInvitedFriend_SeesNothingOnceTheSessionIsPrivate_D007()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);
        Viewer friend = TestViewers.FriendOf(s.Organizer);

        s.Session.ChangeVisibility(ContentVisibility.Private, [], clock);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, friend).IsVisible);
    }

    [Fact]
    public void Stranger_NeverInheritsAccessToADiscoverableSession_D007()
    {
        FakeClock clock = TestSessions.Clock();
        SessionUnderTest s = TestSessions.New(clock, inviteeCount: 1);

        Assert.False(VisibilityEngine.ResolveSession(s.Session, TestViewers.Stranger()).IsVisible);
    }
}
