using System.Globalization;
using System.Reflection;
using System.Text;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Text;

/// <summary>
/// Best-effort multilingual text screening for store-safety requirements. It intentionally stays
/// small, deterministic, and dependency-free; it is not represented as comprehensive moderation.
/// </summary>
public sealed class ProfanityFilter : ITextPolicy
{
    private static readonly IReadOnlyList<WordlistTerm> Terms = LoadTerms();

    public TextPolicyResult Check(string value, TextField field, Locale? locale)
    {
        _ = locale;
        string normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (!HasValidShape(normalized, field))
        {
            return TextPolicyResult.Rejected(field, "errors.text.invalid");
        }

        string comparable = NormalizeForMatching(normalized);
        HashSet<string> tokens = Tokenize(normalized)
            .Select(NormalizeForMatching)
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        bool rejected = Terms.Any(term => term.Exact
            ? tokens.Contains(term.Value)
            : comparable.Contains(term.Value, StringComparison.Ordinal));
        return rejected
            ? TextPolicyResult.Rejected(field, "errors.text.profanity")
            : TextPolicyResult.Allowed(normalized);
    }

    private static bool HasValidShape(string value, TextField field)
    {
        (int minimum, int maximum) = field switch
        {
            TextField.Username => (Username.MinLength, Username.MaxLength),
            TextField.DisplayName => (DisplayName.MinLength, DisplayName.MaxLength),
            TextField.GamingHandle => (1, GamingHandle.MaxLength),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
        if (value.Length < minimum || value.Length > maximum)
        {
            return false;
        }

        Rune? first = null;
        Rune? last = null;
        Rune previous = default;
        int punctuationRun = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format)
            {
                return false;
            }

            bool alphaNumeric = Rune.IsLetterOrDigit(rune);
            bool whitespace = Rune.IsWhiteSpace(rune);
            bool punctuation = IsAllowedPunctuation(rune, field);
            if (!alphaNumeric &&
                !(field != TextField.Username && whitespace) &&
                !punctuation)
            {
                return false;
            }

            first ??= rune;
            last = rune;
            if (punctuation)
            {
                punctuationRun = rune == previous ? punctuationRun + 1 : 1;
                if (punctuationRun > 2)
                {
                    return false;
                }
            }
            else
            {
                punctuationRun = 0;
            }

            previous = rune;
        }

        return first is { } firstRune &&
            last is { } lastRune &&
            !IsAllowedPunctuation(firstRune, field) &&
            !IsAllowedPunctuation(lastRune, field);
    }

    private static bool IsAllowedPunctuation(Rune rune, TextField field)
    {
        int value = rune.Value;
        return field switch
        {
            TextField.Username => value is '.' or '_' or '-',
            TextField.DisplayName => value is 39 or '-' or '.',
            TextField.GamingHandle => value is 39 or '-' or '.' or '_' or '#' or '+',
            _ => false,
        };
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        var token = new StringBuilder();
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(rune.ToString());
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }

    private static string NormalizeForMatching(string value)
    {
        string decomposed = value.Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        char previous = '\0';
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            char mapped = char.ToLowerInvariant(character) switch
            {
                '0' => 'o',
                '1' => 'i',
                '3' => 'e',
                '4' => 'a',
                '5' => 's',
                '7' => 't',
                '@' => 'a',
                '$' => 's',
                '!' => 'i',
                var other => other,
            };
            if (!char.IsLetterOrDigit(mapped) || mapped == previous)
            {
                continue;
            }

            result.Append(mapped);
            previous = mapped;
        }

        return result.ToString();
    }

    private static List<WordlistTerm> LoadTerms()
    {
        Assembly assembly = typeof(ProfanityFilter).Assembly;
        var terms = new List<WordlistTerm>();
        foreach (string locale in new[] { "all", "en", "pt-BR", "es" })
        {
            string resource = $"{assembly.GetName().Name}.Text.Wordlists.{locale}.txt";
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Text wordlist '{resource}' was not found.");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                string value = line.Trim();
                if (value.Length == 0 || value.StartsWith('#'))
                {
                    continue;
                }

                bool exact = value[0] == '=';
                string normalized = NormalizeForMatching(exact ? value[1..] : value);
                if (normalized.Length > 0)
                {
                    terms.Add(new WordlistTerm(normalized, exact));
                }
            }
        }

        return terms;
    }

    private sealed record WordlistTerm(string Value, bool Exact);
}
