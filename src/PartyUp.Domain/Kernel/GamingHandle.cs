namespace PartyUp.Domain.Kernel;

public readonly record struct GamingHandle : ITextValue<GamingHandle>
{
    private readonly string? _value;

    private GamingHandle(string value) => _value = value;

    public static int MinLength => 1;

    public static int MaxLength => 64;

    public string Value => _value ?? string.Empty;

    public static GamingHandle Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(GamingHandle)));

    public override string ToString() => Value;
}
