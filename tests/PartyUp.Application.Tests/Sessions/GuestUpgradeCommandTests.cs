using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

/// <summary>
/// D-225 at the application seam: the guest credential is the only proof of identity, the session
/// parent is locked before occupancy is read, and the single extra authorization — a blocked pair
/// with the organizer — is a generic refusal that leaves the seat exactly as it was.
/// </summary>
public sealed class GuestUpgradeCommandTests
{
    [Fact]
    public async Task Upgrade_RePointsTheSeatUnderTheParentLock_D225()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null, visibility: ContentVisibility.Private, invitees: []);
        (GuestLink link, _) = session.CreateGuestLink(fixture.Clock);
        GuestIdentity guest = session.ClaimGuestLink(
            link.Id, DisplayName.Create("Casey"), ParticipationStatus.Going, fixture.Clock);
        _ = session.DequeueEvents();

        UserId newcomer = UserId.New();
        GuestCommandResult result = await UseCase(fixture).UpgradeAsync(session.Id, link.Id, newcomer, default);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ParticipationStatus.Going,
            session.Find(ParticipantRef.ForUser(newcomer))!.Status);
        Assert.Null(session.Find(ParticipantRef.ForGuest(guest.Id)));
        Assert.Equal(GuestLinkState.Upgraded, link.State);

        // A-068 order: the affected registered users are locked first, then the session parent
        // whose occupancy the write reads.
        IReadOnlyCollection<UserId> lockedUsers = Assert.Single(fixture.Locks.LockedUserBatches);
        Assert.Contains(fixture.OrganizerId, lockedUsers);
        Assert.Contains(newcomer, lockedUsers);
        IReadOnlyCollection<SessionId> locked = Assert.Single(fixture.Locks.LockedSessionBatches);
        Assert.Contains(session.Id, locked);
        Assert.Single(fixture.Materializer.Calls);
    }

    [Fact]
    public async Task Upgrade_ByABlockedPairOfTheOrganizer_IsGenericAndLeavesTheSeatUntouched_D225()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null, visibility: ContentVisibility.Private, invitees: []);
        (GuestLink link, _) = session.CreateGuestLink(fixture.Clock);
        GuestIdentity guest = session.ClaimGuestLink(
            link.Id, DisplayName.Create("Casey"), ParticipationStatus.Going, fixture.Clock);
        _ = session.DequeueEvents();

        UserId blocked = UserId.New();
        fixture.Blocks.Add(Block.Create(
            fixture.OrganizerId, blocked, Username.Create("blockeduser"), fixture.Clock));

        GuestCommandResult result = await UseCase(fixture).UpgradeAsync(session.Id, link.Id, blocked, default);

        Assert.False(result.Succeeded);
        Assert.Equal("upgrade_unavailable", result.Code);
        Assert.Equal(ParticipationStatus.Going, session.Find(ParticipantRef.ForGuest(guest.Id))!.Status);
        Assert.Null(session.Find(ParticipantRef.ForUser(blocked)));
        Assert.Equal(GuestLinkState.Active, link.State);
    }

    [Fact]
    public async Task Upgrade_OfAnUnclaimedOrTerminalLink_IsRefusedWithItsOwnCode_D225()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null, visibility: ContentVisibility.Private, invitees: []);
        (GuestLink unclaimed, _) = session.CreateGuestLink(fixture.Clock);
        (GuestLink revoked, _) = session.CreateGuestLink(fixture.Clock);
        GuestIdentity guest = session.ClaimGuestLink(
            revoked.Id, DisplayName.Create("Casey"), ParticipationStatus.Going, fixture.Clock);
        session.RemoveGuest(guest.Id, fixture.Clock);
        _ = session.DequeueEvents();

        UpgradeGuestSessionUseCase useCase = UseCase(fixture);

        GuestCommandResult unclaimedResult =
            await useCase.UpgradeAsync(session.Id, unclaimed.Id, UserId.New(), default);
        Assert.False(unclaimedResult.Succeeded);
        Assert.Equal("guest_link_unclaimed", unclaimedResult.Code);

        GuestCommandResult terminalResult =
            await useCase.UpgradeAsync(session.Id, revoked.Id, UserId.New(), default);
        Assert.False(terminalResult.Succeeded);
        Assert.Equal("guest_link_terminal", terminalResult.Code);
    }

    private static UpgradeGuestSessionUseCase UseCase(SessionTestFixture fixture) =>
        new(
            fixture.Sessions,
            fixture.Locks,
            fixture.Blocks,
            fixture.Materializer,
            fixture.UnitOfWork,
            fixture.Clock);
}
