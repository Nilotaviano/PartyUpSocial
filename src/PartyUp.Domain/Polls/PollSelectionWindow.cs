using NodaTime;

namespace PartyUp.Domain.Polls;

/// <summary>
/// D-088: the organizer holds a 48-hour window after the voting deadline to select an option. Like
/// every window in the model it is half-open — <c>[Deadline, Deadline + 48 h)</c> (§1), so a
/// finalization attempted at exactly the end instant is already too late.
/// </summary>
public static class PollSelectionWindow
{
    public static Duration Length => Duration.FromHours(48);

    public static Instant EndsAt(Instant deadline) => deadline + Length;

    public static bool IsOpen(Instant deadline, Instant now) => now >= deadline && now < EndsAt(deadline);
}
