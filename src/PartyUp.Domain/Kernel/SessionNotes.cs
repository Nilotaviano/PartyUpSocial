namespace PartyUp.Domain.Kernel;

public readonly record struct SessionNotes : ITextValue<SessionNotes>
{
    private readonly string? _value;

    private SessionNotes(string value) => _value = value;

    public static int MinLength => 0;

    public static int MaxLength => 500;

    public static SessionNotes Empty => Create(null);

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => Value.Length == 0;

    public static SessionNotes Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(SessionNotes)));

    public override string ToString() => Value;
}
