using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.FriendGroups;

/// <summary>1..50 trimmed (D-199). Uniqueness per owner is a DB constraint + Application concern, not aggregate state.</summary>
public sealed record GroupName
{
    private const int MinLength = 1;

    private const int MaxLength = 50;

    private GroupName(string value) => Value = value;

    public string Value { get; }

    public static GroupName Create(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length is < MinLength or > MaxLength)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"A friend group name must be between {MinLength} and {MaxLength} characters, but was {trimmed.Length}."));
        }

        return new GroupName(trimmed);
    }

    public override string ToString() => Value;
}
