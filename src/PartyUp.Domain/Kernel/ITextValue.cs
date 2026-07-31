namespace PartyUp.Domain.Kernel;

/// <summary>
/// A trimmed, length-bounded piece of user-authored text (D-096). Charset policy and the
/// profanity filter are application-layer validation over these same types.
/// </summary>
public interface ITextValue<TSelf>
    where TSelf : struct, ITextValue<TSelf>
{
    string Value { get; }

    static abstract int MinLength { get; }

    static abstract int MaxLength { get; }

    static abstract TSelf Create(string? value);
}

internal static class TextValue
{
    internal static string Normalize(string? raw, int minLength, int maxLength, string name)
    {
        string trimmed = raw?.Trim() ?? string.Empty;

        if (trimmed.Length < minLength)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"{name} must be at least {minLength} character(s) long, but was {trimmed.Length}."));
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException(
                DomainError.ValidationFailed,
                FormattableString.Invariant(
                    $"{name} must be at most {maxLength} character(s) long, but was {trimmed.Length}."));
        }

        return trimmed;
    }
}
