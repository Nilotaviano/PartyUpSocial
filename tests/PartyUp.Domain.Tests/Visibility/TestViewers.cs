using PartyUp.Domain.Kernel;
using PartyUp.Domain.Visibility;

namespace PartyUp.Domain.Tests.Visibility;

internal static class TestViewers
{
    /// <summary>A registered viewer with no friendships and no blocks.</summary>
    internal static Viewer Stranger() => Viewer.Create(UserId.New());

    /// <summary>A brand-new viewer who is an Accepted friend of everyone listed.</summary>
    internal static Viewer FriendOf(params UserId[] friends) => Viewer.Create(UserId.New(), friends);

    internal static Viewer Of(UserId id, IEnumerable<UserId>? friends = null, IEnumerable<UserId>? blocked = null) =>
        Viewer.Create(id, friends, blocked);
}
