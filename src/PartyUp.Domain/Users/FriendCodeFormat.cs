namespace PartyUp.Domain.Users;

public static class FriendCodeFormat
{
    public const int Length = 10;

    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public static bool IsWellFormed(string? candidate)
    {
        if (candidate is null || candidate.Length != Length)
        {
            return false;
        }

        foreach (char symbol in candidate)
        {
            if (!Alphabet.Contains(symbol, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
