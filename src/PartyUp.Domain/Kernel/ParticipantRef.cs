namespace PartyUp.Domain.Kernel;

public readonly record struct ParticipantRef : IComparable<ParticipantRef>
{
    private ParticipantRef(UserId? userId, GuestIdentityId? guestIdentityId)
    {
        UserId = userId;
        GuestIdentityId = guestIdentityId;
    }

    public UserId? UserId { get; }

    public GuestIdentityId? GuestIdentityId { get; }

    public bool IsGuest => GuestIdentityId.HasValue;

    public bool IsRegistered => UserId.HasValue;

    /// <summary>Deterministic total-order key for D-119 finalization ties.</summary>
    public Guid Key => UserId?.Value ?? GuestIdentityId?.Value ?? Guid.Empty;

    public static ParticipantRef ForUser(UserId userId) => new(userId, null);

    public static ParticipantRef ForGuest(GuestIdentityId guestIdentityId) => new(null, guestIdentityId);

    public static ParticipantRef Create(UserId? userId, GuestIdentityId? guestIdentityId)
    {
        if (userId.HasValue == guestIdentityId.HasValue)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                "A participant reference must carry exactly one of a user id or a guest identity id.");
        }

        return new ParticipantRef(userId, guestIdentityId);
    }

    public UserId RequireUserId() =>
        UserId ?? throw new DomainException(DomainError.ValidationFailed, "This participant reference is not a registered user.");

    public GuestIdentityId RequireGuestIdentityId() =>
        GuestIdentityId ?? throw new DomainException(DomainError.ValidationFailed, "This participant reference is not a guest.");

    public int CompareTo(ParticipantRef other)
    {
        int byKey = Key.CompareTo(other.Key);
        return byKey != 0 ? byKey : IsGuest.CompareTo(other.IsGuest);
    }

    public static bool operator <(ParticipantRef left, ParticipantRef right) => left.CompareTo(right) < 0;

    public static bool operator <=(ParticipantRef left, ParticipantRef right) => left.CompareTo(right) <= 0;

    public static bool operator >(ParticipantRef left, ParticipantRef right) => left.CompareTo(right) > 0;

    public static bool operator >=(ParticipantRef left, ParticipantRef right) => left.CompareTo(right) >= 0;

    public override string ToString() => IsGuest ? $"guest:{Key}" : $"user:{Key}";
}
