using PartyUp.Domain.Users;

namespace PartyUp.Application.Text;

public interface ITextPolicy
{
    TextPolicyResult Check(string value, TextField field, Locale? locale);
}

public enum TextField
{
    Username,
    DisplayName,
    GamingHandle,
}

public sealed record TextPolicyResult(
    bool IsAllowed,
    string? NormalizedValue,
    TextField? Field,
    string? ErrorKey)
{
    public static TextPolicyResult Allowed(string value) => new(true, value, null, null);

    public static TextPolicyResult Rejected(TextField field, string errorKey) =>
        new(false, null, field, errorKey);
}
