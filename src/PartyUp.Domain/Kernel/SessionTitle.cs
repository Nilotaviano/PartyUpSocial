namespace PartyUp.Domain.Kernel;

public readonly record struct SessionTitle : ITextValue<SessionTitle>
{
    private readonly string? _value;

    private SessionTitle(string value) => _value = value;

    public static int MinLength => 1;

    public static int MaxLength => 80;

    public string Value => _value ?? string.Empty;

    public static SessionTitle Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(SessionTitle)));

    public override string ToString() => Value;
}
