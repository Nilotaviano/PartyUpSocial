using NodaTime;

namespace PartyUp.Domain.Kernel;

// Values in this file are persisted: append only, never renumber.

public enum UserLifecycle
{
    Onboarding = 0,
    Active = 1,
    PendingDeletion = 2,
    Deleted = 3,
}

public enum Platform
{
    Steam = 0,
    PlayStation = 1,
    Xbox = 2,
    NotApplicable = 3,
}

/// <summary>How far ahead of session start a reminder fires (D-082).</summary>
public enum ReminderOffset
{
    Off = 0,
    Min15 = 1,
    Min30 = 2,
    Hours1 = 3,
    Hours3 = 4,
    Hours12 = 5,
    Hours24 = 6,
}

public static class ReminderOffsetExtensions
{
    /// <summary>The D-082 default when a user has expressed no preference.</summary>
    public const ReminderOffset Default = ReminderOffset.Hours1;

    /// <summary>Lead time before session start; <c>null</c> when reminders are off.</summary>
    public static Duration? ToLeadTime(this ReminderOffset offset) => offset switch
    {
        ReminderOffset.Off => null,
        ReminderOffset.Min15 => Duration.FromMinutes(15),
        ReminderOffset.Min30 => Duration.FromMinutes(30),
        ReminderOffset.Hours1 => Duration.FromHours(1),
        ReminderOffset.Hours3 => Duration.FromHours(3),
        ReminderOffset.Hours12 => Duration.FromHours(12),
        ReminderOffset.Hours24 => Duration.FromHours(24),
        _ => throw new DomainException(
            DomainError.ValidationFailed,
            FormattableString.Invariant($"Unknown reminder offset '{offset}'.")),
    };

    /// <summary>
    /// The instant a reminder should fire for a session starting at <paramref name="startTime"/>;
    /// <c>null</c> when reminders are off (§9 also skips instants already in the past).
    /// </summary>
    public static Instant? ReminderInstantFor(this ReminderOffset offset, Instant startTime) =>
        offset.ToLeadTime() is { } leadTime ? startTime - leadTime : null;
}
