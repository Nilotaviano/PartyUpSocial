namespace PartyUp.Domain.Kernel;

public readonly record struct UserId(Guid Value) : ITypedId<UserId>
{
    public static UserId New() => new(Guid.CreateVersion7());

    public static UserId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct FriendshipId(Guid Value) : ITypedId<FriendshipId>
{
    public static FriendshipId New() => new(Guid.CreateVersion7());

    public static FriendshipId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct BlockId(Guid Value) : ITypedId<BlockId>
{
    public static BlockId New() => new(Guid.CreateVersion7());

    public static BlockId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct PlatformLinkId(Guid Value) : ITypedId<PlatformLinkId>
{
    public static PlatformLinkId New() => new(Guid.CreateVersion7());

    public static PlatformLinkId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct AvatarSourceId(Guid Value) : ITypedId<AvatarSourceId>
{
    public static AvatarSourceId New() => new(Guid.CreateVersion7());

    public static AvatarSourceId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct SessionId(Guid Value) : ITypedId<SessionId>
{
    public static SessionId New() => new(Guid.CreateVersion7());

    public static SessionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct ParticipationId(Guid Value) : ITypedId<ParticipationId>
{
    public static ParticipationId New() => new(Guid.CreateVersion7());

    public static ParticipationId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct JoinRequestId(Guid Value) : ITypedId<JoinRequestId>
{
    public static JoinRequestId New() => new(Guid.CreateVersion7());

    public static JoinRequestId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct GuestIdentityId(Guid Value) : ITypedId<GuestIdentityId>
{
    public static GuestIdentityId New() => new(Guid.CreateVersion7());

    public static GuestIdentityId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct GuestLinkId(Guid Value) : ITypedId<GuestLinkId>
{
    public static GuestLinkId New() => new(Guid.CreateVersion7());

    public static GuestLinkId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct PollId(Guid Value) : ITypedId<PollId>
{
    public static PollId New() => new(Guid.CreateVersion7());

    public static PollId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct PollOptionId(Guid Value) : ITypedId<PollOptionId>
{
    public static PollOptionId New() => new(Guid.CreateVersion7());

    public static PollOptionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct PollParticipantId(Guid Value) : ITypedId<PollParticipantId>
{
    public static PollParticipantId New() => new(Guid.CreateVersion7());

    public static PollParticipantId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct AvailabilitySeriesId(Guid Value) : ITypedId<AvailabilitySeriesId>
{
    public static AvailabilitySeriesId New() => new(Guid.CreateVersion7());

    public static AvailabilitySeriesId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct AvailabilityBlockId(Guid Value) : ITypedId<AvailabilityBlockId>
{
    public static AvailabilityBlockId New() => new(Guid.CreateVersion7());

    public static AvailabilityBlockId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct NotificationId(Guid Value) : ITypedId<NotificationId>
{
    public static NotificationId New() => new(Guid.CreateVersion7());

    public static NotificationId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct NotificationMuteId(Guid Value) : ITypedId<NotificationMuteId>
{
    public static NotificationMuteId New() => new(Guid.CreateVersion7());

    public static NotificationMuteId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct PushSubscriptionId(Guid Value) : ITypedId<PushSubscriptionId>
{
    public static PushSubscriptionId New() => new(Guid.CreateVersion7());

    public static PushSubscriptionId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}

public readonly record struct FeedbackItemId(Guid Value) : ITypedId<FeedbackItemId>
{
    public static FeedbackItemId New() => new(Guid.CreateVersion7());

    public static FeedbackItemId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}
