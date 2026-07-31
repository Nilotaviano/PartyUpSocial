using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// The stable, unique code behind profile sharing and QR resolution (D-031/D-144). Canonical form
/// is trimmed and upper-cased so lookup is case-insensitive; generation belongs to the application
/// layer, which owns uniqueness retries.
/// </summary>
public readonly record struct FriendCode : ITextValue<FriendCode>
{
    private readonly string? _value;

    private FriendCode(string value) => _value = value;

    public static int MinLength => 6;

    public static int MaxLength => 32;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => Value.Length == 0;

    public static FriendCode Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(FriendCode)).ToUpperInvariant());

    public override string ToString() => Value;
}
