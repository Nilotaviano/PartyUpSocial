using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Infrastructure.Friends;

public sealed class PublicLinkBuilder : IPublicLinkBuilder
{
    private readonly string _baseUrl;

    public PublicLinkBuilder(string publicBaseUrl)
    {
        string candidate = publicBaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "PublicBaseUrl must be an absolute HTTP or HTTPS URL without a query or fragment.",
                nameof(publicBaseUrl));
        }

        _baseUrl = candidate;
    }

    public string Profile(Username username) =>
        $"{_baseUrl}/u/{Uri.EscapeDataString(username.Value)}";

    public string Add(FriendCode friendCode)
    {
        if (!FriendCodeFormat.IsWellFormed(friendCode.Value))
        {
            throw new ArgumentException("The friend code is not canonical.", nameof(friendCode));
        }

        return $"{_baseUrl}/add/{friendCode.Value}";
    }
}
