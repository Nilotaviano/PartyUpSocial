using PartyUp.Domain.Users;

namespace PartyUp.Application.Email;

public sealed record EmailMessage(
    string ToAddress,
    Locale Locale,
    EmailTemplate Template,
    IReadOnlyDictionary<string, string> Model);