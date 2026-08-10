using NodaTime;
using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// The account aggregate (DOMAIN_MODEL §3). Both <c>Onboarding</c> and <c>Active</c> may enter
/// <c>PendingDeletion</c>, and an elapsed grace ends in <c>Deleted</c>. Profile fields are nullable
/// in <c>Onboarding</c> and in the <c>Deleted</c> tombstone only.
/// </summary>
public sealed class User : AggregateRoot
{
    private readonly List<PlatformLink> _platformLinks = [];

    private User()
    {
    }

    private User(UserId id, FriendCode friendCode, AgeGateSource ageGateSource, Instant createdAt)
    {
        Id = id;
        FriendCode = friendCode;
        AgeGatePassed = true;
        AgeGateSource = ageGateSource;
        Lifecycle = UserLifecycle.Onboarding;
        ReminderDefault = ReminderOffsetExtensions.Default;
        CreatedAt = createdAt;
    }

    /// <summary>D-148: deletion is cancelable for seven days before any destructive effect.</summary>
    public static Duration DeletionGracePeriod => Duration.FromDays(7);

    /// <summary>D-128: a username may change once every thirty days.</summary>
    public static Duration UsernameChangeCooldown => Duration.FromDays(30);

    public UserId Id { get; private set; }

    public UserLifecycle Lifecycle { get; private set; }

    public Username? Username { get; private set; }

    public DisplayName? DisplayName { get; private set; }

    public Avatar? Avatar { get; private set; }

    public DateTimeZone? TimeZone { get; private set; }

    public Locale? Locale { get; private set; }

    /// <summary>Stable across username changes (D-128/D-144); retired by the tombstone.</summary>
    public FriendCode? FriendCode { get; private set; }

    public bool AgeGatePassed { get; private set; }

    /// <summary>D-093 records only the signal's source — never a birth date.</summary>
    public AgeGateSource? AgeGateSource { get; private set; }

    /// <summary>D-082, defaulting to one hour.</summary>
    public ReminderOffset ReminderDefault { get; private set; } = ReminderOffsetExtensions.Default;

    public Instant CreatedAt { get; private set; }

    /// <summary>Null until the first canonical username change; anchors the D-128 cooldown.</summary>
    public Instant? UsernameChangedAt { get; private set; }

    public Instant? GraceEndsAt { get; private set; }

    public Instant? DeletedAt { get; private set; }

    /// <summary>D-180: orthogonal to the lifecycle. A banned user keeps their content and history.</summary>
    public Instant? BannedAt { get; private set; }

    /// <summary>
    /// D-182: null until the account finishes or skips the first-login tour; a profile fact like
    /// <see cref="ReminderDefault"/>, not a lifecycle value. Account-grained (any device), so no
    /// device-local flag exists alongside it.
    /// </summary>
    public Instant? TourCompletedAt { get; private set; }

    public IReadOnlyList<PlatformLink> PlatformLinks => _platformLinks;

    public bool IsOnboarding => Lifecycle == UserLifecycle.Onboarding;

    public bool IsActive => Lifecycle == UserLifecycle.Active;

    public bool IsPendingDeletion => Lifecycle == UserLifecycle.PendingDeletion;

    public bool IsTombstoned => Lifecycle == UserLifecycle.Deleted;

    public bool IsBanned => BannedAt is not null;

    /// <summary>
    /// DOMAIN_MODEL §12-22 and §3: an <c>Onboarding</c> user's half-built profile must not surface,
    /// and a tombstone is excluded from every lookup surface. The grace period has no domain
    /// effects (§12-34), so a <c>PendingDeletion</c> user is still discoverable.
    /// </summary>
    public bool IsDiscoverable => Lifecycle is UserLifecycle.Active or UserLifecycle.PendingDeletion;

    /// <summary>Same set as <see cref="IsDiscoverable"/>: an invisible user cannot be friended.</summary>
    public bool CanBeFriended => IsDiscoverable && !IsBanned;

    public bool IsProfileComplete =>
        Username.HasValue && DisplayName.HasValue && Avatar.HasValue && TimeZone is not null && Locale.HasValue;

    /// <summary>D-127: the account exists after the age gate and authentication, before the profile.</summary>
    public static User BeginOnboarding(
        UserId id,
        FriendCode friendCode,
        AgeGateSource ageGateSource,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireFriendCode(friendCode);
        RequireDefinedAgeGateSource(ageGateSource);

        return new User(id, friendCode, ageGateSource, clock.GetCurrentInstant());
    }

    /// <summary>
    /// D-127: the profile step. Every field is required — this is the only door into
    /// <c>Active</c>, so <c>Active</c> can never hold a partial profile.
    /// </summary>
    public void CompleteOnboarding(
        Username username,
        DisplayName displayName,
        Avatar avatar,
        DateTimeZone timeZone,
        Locale locale,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsOnboarding)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Onboarding can only be completed from Onboarding, but the user is {Lifecycle}."));
        }

        RequireUsername(username);
        RequireDisplayName(displayName);
        RequireAvatar(avatar);
        RequireLocale(locale);

        Username = username;
        DisplayName = displayName;
        Avatar = avatar;
        TimeZone = timeZone;
        Locale = locale;
        Lifecycle = UserLifecycle.Active;

        Raise(new UserOnboardingCompleted(Id));
    }

    /// <summary>D-128: true when no canonical username change has happened inside the cooldown.</summary>
    public bool CanChangeUsername(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return UsernameChangedAt is not { } changedAt
            || clock.GetCurrentInstant() >= changedAt + UsernameChangeCooldown;
    }

    /// <summary>
    /// D-128. Re-styling the capitalisation of the same canonical name is not a change: it claims
    /// no new registry row, so it neither consumes nor is gated by the cooldown.
    /// </summary>
    public void ChangeUsername(Username newUsername, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        RequireActive("change the username");
        RequireUsername(newUsername);

        bool canonicalChanges = !UsernameRegistry.SameName(Username!.Value, newUsername);

        if (canonicalChanges && !CanChangeUsername(clock))
        {
            throw new DomainException(
                DomainError.CooldownActive,
                FormattableString.Invariant(
                    $"The username can change again at {UsernameChangedAt!.Value + UsernameChangeCooldown}."));
        }

        Username = newUsername;

        if (canonicalChanges)
        {
            UsernameChangedAt = clock.GetCurrentInstant();
        }

        Raise(new UserUsernameChanged(Id));
    }

    public void ChangeDisplayName(DisplayName displayName)
    {
        RequireActive("change the display name");
        RequireDisplayName(displayName);

        DisplayName = displayName;
    }

    /// <summary>D-050: a platform avatar must point at one of this user's own links.</summary>
    public void ChangeAvatar(Avatar avatar)
    {
        RequireActive("change the avatar");
        RequireAvatar(avatar);

        Avatar = avatar;
    }

    public void ChangeTimeZone(DateTimeZone timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        RequireActive("change the time zone");

        TimeZone = timeZone;
    }

    public void ChangeLocale(Locale locale)
    {
        RequireActive("change the locale");
        RequireLocale(locale);

        Locale = locale;
    }

    /// <summary>D-082.</summary>
    public void SetReminderDefault(ReminderOffset reminderDefault)
    {
        RequireProfileMutable("change the reminder default");

        if (!Enum.IsDefined(reminderDefault))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown reminder offset '{reminderDefault}'."));
        }

        ReminderDefault = reminderDefault;
    }

    /// <summary>
    /// D-182: first instant wins, idempotent — both finishing and skipping call this, and a second
    /// call (a retried POST, or Replay's completion no-op on an already-completed account) never
    /// moves the instant.
    /// </summary>
    public void CompleteTour(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (TourCompletedAt is not null)
        {
            return;
        }

        TourCompletedAt = clock.GetCurrentInstant();
    }

    /// <summary>D-032/D-127: platform linking is available during onboarding and afterwards.</summary>
    public PlatformLink LinkPlatform(Platform platform, GamingHandle handle)
    {
        RequireProfileMutable("link a platform");

        if (_platformLinks.Exists(link => link.Platform == platform))
        {
            throw new DomainException(
                DomainError.Duplicate,
                FormattableString.Invariant($"Platform '{platform}' is already linked."));
        }

        PlatformLink created = PlatformLink.Create(Id, platform, handle);
        _platformLinks.Add(created);
        return created;
    }

    public void ChangePlatformHandle(PlatformLinkId platformLinkId, GamingHandle handle)
    {
        RequireProfileMutable("change a platform handle");

        RequireLink(platformLinkId).ChangeHandle(handle);
    }

    /// <summary>
    /// D-050: unlinking the avatar-source platform falls the avatar back to emoji selection, so the
    /// <c>Active</c> profile never loses a required field.
    /// </summary>
    public void UnlinkPlatform(PlatformLinkId platformLinkId, string? fallbackEmoji = null)
    {
        RequireProfileMutable("unlink a platform");

        PlatformLink link = RequireLink(platformLinkId);
        _platformLinks.Remove(link);

        if (Avatar is { } avatar && avatar.DrawsFrom(platformLinkId))
        {
            Avatar = fallbackEmoji is null
                ? PartyUp.Domain.Users.Avatar.Fallback
                : PartyUp.Domain.Users.Avatar.FromEmoji(fallbackEmoji);
        }
    }

    /// <summary>D-180: bans preserve lifecycle, content and history while disabling account access.</summary>
    public void Ban(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (IsTombstoned)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "A tombstoned user cannot be banned.");
        }

        if (IsBanned)
        {
            return;
        }

        BannedAt = clock.GetCurrentInstant();
        Raise(new UserBanned(Id, BannedAt.Value));
    }

    public void Unban()
    {
        if (IsTombstoned)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                "A tombstoned user cannot be unbanned.");
        }

        if (!IsBanned)
        {
            return;
        }

        BannedAt = null;
        Raise(new UserUnbanned(Id));
    }

    /// <summary>D-148: a seven-day cancelable grace period; nothing destructive happens yet.</summary>
    public void RequestDeletion(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Lifecycle is not (UserLifecycle.Onboarding or UserLifecycle.Active))
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant(
                    $"Deletion can only be requested by an Onboarding or Active user, but the user is {Lifecycle}."));
        }

        Instant now = clock.GetCurrentInstant();
        GraceEndsAt = now + DeletionGracePeriod;
        Lifecycle = UserLifecycle.PendingDeletion;

        Raise(new UserDeletionRequested(Id, GraceEndsAt.Value));
    }

    /// <summary>
    /// D-158/D-165: a successful login inside the grace period cancels the deletion. Clock-gated
    /// like every window (DOMAIN_MODEL §1) — a late sweeper never extends it.
    /// </summary>
    public void CancelDeletion(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!IsPendingDeletion)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Only a PendingDeletion user can cancel deletion, but the user is {Lifecycle}."));
        }

        if (IsGraceElapsed(clock))
        {
            throw new DomainException(
                DomainError.WindowClosed,
                "The seven-day deletion grace period has elapsed.");
        }

        GraceEndsAt = null;
        Lifecycle = IsProfileComplete ? UserLifecycle.Active : UserLifecycle.Onboarding;

        Raise(new UserDeletionCanceled(Id));
    }

    public bool IsGraceElapsed(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return GraceEndsAt is { } graceEndsAt && clock.GetCurrentInstant() >= graceEndsAt;
    }

    /// <summary>
    /// D-092 step 6 (DOMAIN_MODEL §11): nulls the PII, drops the platform links and retires the
    /// friend code, while the row and <see cref="Id"/> stay FK-stable so history survives. Retiring
    /// the username into its thirty-day reservation is the registry's job
    /// (<see cref="UsernameRegistry.Release"/>) and is coordinated by the deletion executor.
    /// Idempotent: re-running the sweep over a tombstone is a no-op.
    /// </summary>
    public void MarkDeleted(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (IsTombstoned)
        {
            return;
        }

        if (!IsPendingDeletion)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Only a PendingDeletion user can be tombstoned, but the user is {Lifecycle}."));
        }

        if (!IsGraceElapsed(clock))
        {
            throw new DomainException(
                DomainError.WindowClosed,
                FormattableString.Invariant($"The deletion grace period runs until {GraceEndsAt!.Value}."));
        }

        Instant now = clock.GetCurrentInstant();

        Username = null;
        DisplayName = null;
        Avatar = null;
        FriendCode = null;
        _platformLinks.Clear();
        GraceEndsAt = null;
        DeletedAt = now;
        Lifecycle = UserLifecycle.Deleted;

        Raise(new UserTombstoned(Id, now));
    }

    private static void RequireUsername(Username username)
    {
        if (username.Value.Length < Kernel.Username.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A username is required.");
        }
    }

    private static void RequireDisplayName(DisplayName displayName)
    {
        if (displayName.Value.Length < Kernel.DisplayName.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A display name is required.");
        }
    }

    private static void RequireLocale(Locale locale)
    {
        if (locale.Value.Length < PartyUp.Domain.Users.Locale.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A locale is required.");
        }
    }

    private static void RequireFriendCode(FriendCode friendCode)
    {
        if (friendCode.Value.Length < PartyUp.Domain.Users.FriendCode.MinLength)
        {
            throw new DomainException(DomainError.ValidationFailed, "A friend code is required.");
        }
    }

    private static void RequireDefinedAgeGateSource(AgeGateSource ageGateSource)
    {
        if (!Enum.IsDefined(ageGateSource))
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant($"Unknown age gate source '{ageGateSource}'."));
        }
    }

    private void RequireAvatar(Avatar avatar)
    {
        if (avatar.IsUnset)
        {
            throw new DomainException(DomainError.ValidationFailed, "An avatar is required.");
        }

        if (avatar.IsPlatform && !_platformLinks.Exists(link => link.Id == avatar.SourcePlatformLinkId))
        {
            throw new DomainException(
                DomainError.NotFound,
                "A platform avatar must point at one of this user's own platform links.");
        }
    }

    private PlatformLink RequireLink(PlatformLinkId platformLinkId) =>
        _platformLinks.Find(link => link.Id == platformLinkId)
        ?? throw new DomainException(DomainError.NotFound, "This user has no such platform link.");

    private void RequireActive(string action)
    {
        if (!IsActive)
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Only an Active user can {action}, but the user is {Lifecycle}."));
        }
    }

    private void RequireProfileMutable(string action)
    {
        if (Lifecycle is not (UserLifecycle.Onboarding or UserLifecycle.Active))
        {
            throw new DomainException(
                DomainError.InvalidTransition,
                FormattableString.Invariant($"Only an Onboarding or Active user can {action}, but the user is {Lifecycle}."));
        }
    }
}
