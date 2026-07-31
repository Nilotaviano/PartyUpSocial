namespace PartyUp.Domain.Kernel;

/// <summary>Free-text game title (D-041/D-058/D-176); catalog selection carries the same bounds.</summary>
public readonly record struct GameName : ITextValue<GameName>
{
    private readonly string? _value;

    private GameName(string value) => _value = value;

    public static int MinLength => 1;

    public static int MaxLength => 100;

    public string Value => _value ?? string.Empty;

    public static GameName Create(string? value) =>
        new(TextValue.Normalize(value, MinLength, MaxLength, nameof(GameName)));

    public override string ToString() => Value;
}
