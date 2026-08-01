using System.Globalization;
using System.Text;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Auth;

public static class FriendContinuation
{
    private const string UsernamePrefix = "/u/";
    private const string FriendCodePrefix = "/add/";

    public static string? Validate(string? continuation)
    {
        if (string.IsNullOrEmpty(continuation) ||
            continuation.Contains('?', StringComparison.Ordinal) ||
            continuation.Contains('#', StringComparison.Ordinal) ||
            continuation.Contains('%', StringComparison.Ordinal) ||
            continuation.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (continuation.StartsWith(UsernamePrefix, StringComparison.Ordinal))
        {
            string username = continuation[UsernamePrefix.Length..]
                .Normalize(NormalizationForm.FormC);
            return IsValidUsername(username)
                ? UsernamePrefix + UsernameRegistry.Canonicalize(username)
                : null;
        }

        if (continuation.StartsWith(FriendCodePrefix, StringComparison.Ordinal))
        {
            string friendCode = ResolveFriendCodeUseCase.Normalize(
                continuation[FriendCodePrefix.Length..]);
            return FriendCodeFormat.IsWellFormed(friendCode)
                ? FriendCodePrefix + friendCode
                : null;
        }

        return null;
    }

    private static bool IsValidUsername(string username)
    {
        if (username.Length < Username.MinLength ||
            username.Length > Username.MaxLength ||
            username.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        Rune? first = null;
        Rune? last = null;
        Rune previous = default;
        int punctuationRun = 0;
        foreach (Rune rune in username.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                return false;
            }

            bool punctuation = IsUsernamePunctuation(rune);
            if (!Rune.IsLetterOrDigit(rune) && !punctuation)
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
            !IsUsernamePunctuation(firstRune) &&
            !IsUsernamePunctuation(lastRune);
    }

    private static bool IsUsernamePunctuation(Rune rune) =>
        rune.Value is '.' or '_' or '-';
}
