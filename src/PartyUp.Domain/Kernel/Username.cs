namespace PartyUp.Domain.Kernel;

public readonly record struct Username : ITextValue<Username>
{
    private readonly string? _value;

    private Username(string value) => _value = value;

    public static int MinLength => 3;

    public static int MaxLength => 30;

    public string Value => _value ?? string.Empty;

    public static Username Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(Username)));

    public override string ToString() => Value;
}
