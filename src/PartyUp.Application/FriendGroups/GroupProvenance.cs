using PartyUp.Domain.FriendGroups;

namespace PartyUp.Application.FriendGroups;

/// <summary>
/// D-206's "via &lt;group&gt;" provenance on a session/poll read: the targeted group that grants THIS
/// viewer rule-2g access, read from the live group row so a rename is picked up without a rewrite.
/// Non-null only for a viewer who owns or holds Accepted membership in that group — every other
/// viewer of a Groups target sees the neutral "shared with a group" the API derives from
/// <c>Visibility == Groups</c> with a null provenance. Group names are never serialized to
/// non-members; this record is the only channel that carries one, and it is only ever populated for
/// a member.
/// </summary>
public sealed record GroupProvenance(FriendGroupId GroupId, GroupName Name);
