namespace PartyUp.Domain.Kernel;

/// <summary>Registered users and guests share these bounds (DOMAIN_MODEL §1, §5.4).</summary>
public readonly record struct DisplayName : ITextValue<DisplayName>
{
    private readonly string? _value;

    private DisplayName(string value) => _value = value;

    public static int MinLength => 1;

    public static int MaxLength => 50;

    public string Value => _value ?? string.Empty;

    public static DisplayName Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(DisplayName)));

    public override string ToString() => Value;
}
