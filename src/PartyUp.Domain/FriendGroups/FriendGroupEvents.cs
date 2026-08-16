using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.FriendGroups;

// Domain events carry ids only — never names or rendered text (DOMAIN_MODEL §9).

public sealed record FriendGroupInviteSent(
    FriendGroupId FriendGroupId,
    UserId OwnerId,
    UserId InviteeId) : IDomainEvent;

public sealed record FriendGroupInviteAccepted(
    FriendGroupId FriendGroupId,
    UserId OwnerId,
    UserId InviteeId) : IDomainEvent;
