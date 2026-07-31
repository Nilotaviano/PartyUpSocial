using PartyUp.Domain.Kernel;

namespace PartyUp.Domain.Users;

/// <summary>
/// The BCP-47 tag every notification and email is rendered in (plan principle 6). The domain only
/// bounds the shape; which tags are supported is application configuration.
/// </summary>
public readonly record struct Locale : ITextValue<Locale>
{
    private readonly string? _value;

    private Locale(string value) => _value = value;

    public static int MinLength => 2;

    public static int MaxLength => 35;

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => Value.Length == 0;

    public static Locale Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(Locale)));

    public override string ToString() => Value;
}
