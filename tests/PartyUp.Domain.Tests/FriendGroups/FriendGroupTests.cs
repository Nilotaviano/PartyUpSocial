using NodaTime;
using NodaTime.Testing;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Tests.FriendGroups;

public class FriendGroupTests
{
    private static readonly Instant Origin = Instant.FromUtc(2026, 8, 16, 12, 0);

    private static readonly UserId Owner = UserId.New();

    private static FakeClock Clock() => new(Origin);

    [Fact]
    public void Create_SetsOwnerAndNameWithNoMemberRows_D199()
    {
        UserId owner = Owner;
        FakeClock clock = Clock();

        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        Assert.Equal(owner, group.OwnerId);
        Assert.Equal("D&D crew", group.Name.Value);
        Assert.Equal(Origin, group.CreatedAt);
        Assert.Empty(group.Members);
    }

    [Fact]
    public void Rename_ByNonOwner_ThrowsForbidden_D199()
    {
        UserId owner = Owner;
        UserId stranger = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Rename(stranger, GroupName.Create("New name")));

        Assert.Equal(DomainError.Forbidden, ex.Error);
    }

    [Fact]
    public void Invite_ByNonOwner_ThrowsForbidden_D199()
    {
        UserId owner = Owner;
        UserId stranger = UserId.New();
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Invite(stranger, invitee, isAcceptedFriendOfOwner: true, clock));

        Assert.Equal(DomainError.Forbidden, ex.Error);
    }

    [Fact]
    public void RevokeInvite_ByNonOwner_ThrowsForbidden_D199()
    {
        UserId owner = Owner;
        UserId stranger = UserId.New();
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.RevokeInvite(stranger, invitee));

        Assert.Equal(DomainError.Forbidden, ex.Error);
    }

    [Fact]
    public void RemoveMember_ByNonOwner_ThrowsForbidden_D199()
    {
        UserId owner = Owner;
        UserId stranger = UserId.New();
        UserId member = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock);
        group.AcceptInvite(member, clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.RemoveMember(stranger, member));

        Assert.Equal(DomainError.Forbidden, ex.Error);
    }

    [Fact]
    public void Invite_Self_ThrowsValidationFailed()
    {
        UserId owner = Owner;
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Invite(owner, owner, isAcceptedFriendOfOwner: true, clock));

        Assert.Equal(DomainError.ValidationFailed, ex.Error);
    }

    [Fact]
    public void Invite_RequiresAnAcceptedFriendshipAtInviteTime_D200()
    {
        UserId owner = Owner;
        UserId stranger = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Invite(owner, stranger, isAcceptedFriendOfOwner: false, clock));

        Assert.Equal(DomainError.NotFound, ex.Error); // identical to unknown user — no block oracle (D-202)
    }

    [Fact]
    public void Invite_Duplicate_ThrowsDuplicate()
    {
        UserId owner = Owner;
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock));

        Assert.Equal(DomainError.Duplicate, ex.Error);
    }

    [Fact]
    public void Invite_AlreadyAcceptedMember_ThrowsDuplicate()
    {
        UserId owner = Owner;
        UserId member = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock);
        group.AcceptInvite(member, clock);

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock));

        Assert.Equal(DomainError.Duplicate, ex.Error);
    }

    [Fact]
    public void Invite_AtTheHundredRowCap_ThrowsLimitExceeded_D208()
    {
        UserId owner = Owner;
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        for (int i = 0; i < FriendGroup.MaxMembers; i++)
        {
            group.Invite(owner, UserId.New(), isAcceptedFriendOfOwner: true, clock);
        }

        DomainException ex = Assert.Throws<DomainException>(
            () => group.Invite(owner, UserId.New(), isAcceptedFriendOfOwner: true, clock));

        Assert.Equal(DomainError.LimitExceeded, ex.Error);
        Assert.Equal(FriendGroup.MaxMembers, group.Members.Count);
        Assert.Equal(100, FriendGroup.MaxMembers);
    }

    [Fact]
    public void Invite_RaisesFriendGroupInviteSent()
    {
        UserId owner = Owner;
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);

        FriendGroupInviteSent raised = Assert.Single(group.DequeueEvents().OfType<FriendGroupInviteSent>());
        Assert.Equal(group.Id, raised.FriendGroupId);
        Assert.Equal(owner, raised.OwnerId);
        Assert.Equal(invitee, raised.InviteeId);
    }

    [Fact]
    public void AcceptInvite_FlipsInvitedToAcceptedAndRaisesEvent_D200()
    {
        UserId owner = Owner;
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);
        group.DequeueEvents();

        group.AcceptInvite(invitee, clock);

        FriendGroupMember member = Assert.Single(group.Members);
        Assert.Equal(invitee, member.UserId);
        Assert.Equal(FriendGroupMemberStatus.Accepted, member.Status);
        Assert.Equal(Origin, member.AcceptedAt);
        FriendGroupInviteAccepted raised = Assert.Single(group.DequeueEvents().OfType<FriendGroupInviteAccepted>());
        Assert.Equal(group.Id, raised.FriendGroupId);
        Assert.Equal(owner, raised.OwnerId);
        Assert.Equal(invitee, raised.InviteeId);
    }

    [Fact]
    public void AcceptInvite_ByNonInvitedUser_ThrowsNotFound()
    {
        UserId owner = Owner;
        UserId stranger = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        DomainException ex = Assert.Throws<DomainException>(() => group.AcceptInvite(stranger, clock));

        Assert.Equal(DomainError.NotFound, ex.Error);
    }

    [Fact]
    public void DeclineInvite_DeletesTheRow_D200()
    {
        UserId owner = Owner;
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);
        group.DequeueEvents();

        group.DeclineInvite(invitee);

        Assert.DoesNotContain(group.Members, m => m.UserId == invitee);
        Assert.Empty(group.DequeueEvents());
    }

    [Fact]
    public void RevokeInvite_DeletesTheRow_D200()
    {
        UserId owner = Owner;
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);
        group.DequeueEvents();

        group.RevokeInvite(owner, invitee);

        Assert.DoesNotContain(group.Members, m => m.UserId == invitee);
        Assert.Empty(group.DequeueEvents());
    }

    [Fact]
    public void Leave_DeletesTheRow_D200()
    {
        UserId owner = Owner;
        UserId member = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock);
        group.AcceptInvite(member, clock);
        group.DequeueEvents();

        group.Leave(member);

        Assert.DoesNotContain(group.Members, m => m.UserId == member);
        Assert.Empty(group.DequeueEvents());
    }

    [Fact]
    public void RemoveMember_DeletesTheRow_D200()
    {
        UserId owner = Owner;
        UserId member = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock);
        group.AcceptInvite(member, clock);
        group.DequeueEvents();

        group.RemoveMember(owner, member);

        Assert.DoesNotContain(group.Members, m => m.UserId == member);
        Assert.Empty(group.DequeueEvents());
    }

    /// <summary>
    /// D-207: decline, revoke, leave and remove are all silent — no notification-worthy event for
    /// any of them, unlike invite and accept. One fact, four independent groups, so this is the
    /// D-207-greppable proof rather than a side effect buried in each `_D200` row-deletion test.
    /// </summary>
    [Fact]
    public void DeclineRevokeLeaveRemove_RaiseNoEvents_D207()
    {
        UserId owner = Owner;
        FakeClock clock = Clock();

        FriendGroup declined = FriendGroup.Create(owner, GroupName.Create("Declined"), clock);
        UserId decliner = UserId.New();
        declined.Invite(owner, decliner, isAcceptedFriendOfOwner: true, clock);
        declined.DequeueEvents();
        declined.DeclineInvite(decliner);
        Assert.Empty(declined.DequeueEvents());

        FriendGroup revoked = FriendGroup.Create(owner, GroupName.Create("Revoked"), clock);
        UserId revokee = UserId.New();
        revoked.Invite(owner, revokee, isAcceptedFriendOfOwner: true, clock);
        revoked.DequeueEvents();
        revoked.RevokeInvite(owner, revokee);
        Assert.Empty(revoked.DequeueEvents());

        FriendGroup left = FriendGroup.Create(owner, GroupName.Create("Left"), clock);
        UserId leaver = UserId.New();
        left.Invite(owner, leaver, isAcceptedFriendOfOwner: true, clock);
        left.AcceptInvite(leaver, clock);
        left.DequeueEvents();
        left.Leave(leaver);
        Assert.Empty(left.DequeueEvents());

        FriendGroup removed = FriendGroup.Create(owner, GroupName.Create("Removed"), clock);
        UserId removee = UserId.New();
        removed.Invite(owner, removee, isAcceptedFriendOfOwner: true, clock);
        removed.AcceptInvite(removee, clock);
        removed.DequeueEvents();
        removed.RemoveMember(owner, removee);
        Assert.Empty(removed.DequeueEvents());
    }

    [Fact]
    public void Leave_ByOwner_ThrowsInvalidTransition()
    {
        UserId owner = Owner;
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        DomainException ex = Assert.Throws<DomainException>(() => group.Leave(owner));

        Assert.Equal(DomainError.InvalidTransition, ex.Error);
    }

    [Fact]
    public void RemoveCounterpartOf_Owner_DeletesInvitedRowSilentlyWithNoEvent_D202()
    {
        UserId owner = Owner;
        UserId invitee = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, invitee, isAcceptedFriendOfOwner: true, clock);
        group.DequeueEvents();

        group.RemoveCounterpartOf(owner, invitee);

        Assert.DoesNotContain(group.Members, m => m.UserId == invitee);
        Assert.Empty(group.DequeueEvents());
    }

    [Fact]
    public void RemoveCounterpartOf_Owner_DeletesAcceptedRowSilentlyWithNoEvent_D202()
    {
        UserId owner = Owner;
        UserId member = UserId.New();
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);
        group.Invite(owner, member, isAcceptedFriendOfOwner: true, clock);
        group.AcceptInvite(member, clock);
        group.DequeueEvents();

        group.RemoveCounterpartOf(member, owner);

        Assert.DoesNotContain(group.Members, m => m.UserId == member);
        Assert.Empty(group.DequeueEvents());
    }

    [Fact]
    public void HasAcceptedMember_Owner_TrueWithNoRow_D198()
    {
        UserId owner = Owner;
        FakeClock clock = Clock();
        FriendGroup group = FriendGroup.Create(owner, GroupName.Create("D&D crew"), clock);

        Assert.True(group.HasAcceptedMember(owner));
        Assert.Empty(group.Members);
    }

    [Fact]
    public void GroupName_Empty_ThrowsValidationFailed_D199()
    {
        DomainException ex = Assert.Throws<DomainException>(() => GroupName.Create(""));

        Assert.Equal(DomainError.ValidationFailed, ex.Error);
    }

    [Fact]
    public void GroupName_TooLong_ThrowsValidationFailed_D199()
    {
        string tooLong = new('x', 51);

        DomainException ex = Assert.Throws<DomainException>(() => GroupName.Create(tooLong));

        Assert.Equal(DomainError.ValidationFailed, ex.Error);
    }

    [Fact]
    public void GroupName_Trims_D199()
    {
        GroupName name = GroupName.Create("  D&D crew  ");

        Assert.Equal("D&D crew", name.Value);
    }
}
