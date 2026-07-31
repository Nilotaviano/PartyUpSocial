using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PartyUp.Infrastructure.Steam;

public sealed partial class SteamOpenIdVerifier(HttpClient httpClient)
{
    private const int MaximumRememberedNonces = 4096;

    public const string ProviderEndpoint = "https://steamcommunity.com/openid/login";
    public const string OpenIdNamespace = "http://specs.openid.net/auth/2.0";

    private static readonly string[] RequiredSignedFields =
    [
        "claimed_id", "identity", "return_to", "response_nonce", "assoc_handle", "op_endpoint",
    ];

    private readonly ConcurrentDictionary<string, byte> _acceptedResponseNonces =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _acceptedNonceOrder = new();

    public async Task<SteamOpenIdVerification> VerifyAsync(
        IReadOnlyDictionary<string, string> assertion,
        string expectedReturnTo,
        CancellationToken cancellationToken)
    {
        if (!HasValue(assertion, "openid.ns", OpenIdNamespace) ||
            !HasValue(assertion, "openid.mode", "id_res") ||
            !HasValue(assertion, "openid.op_endpoint", ProviderEndpoint) ||
            !assertion.TryGetValue("openid.return_to", out string? returnTo) ||
            !string.Equals(returnTo, expectedReturnTo, StringComparison.Ordinal) ||
            !assertion.TryGetValue("openid.signed", out string? signedValue))
        {
            return SteamOpenIdVerification.Invalid;
        }

        HashSet<string> signed = signedValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (RequiredSignedFields.Any(field => !signed.Contains(field)) ||
            !assertion.TryGetValue("openid.claimed_id", out string? claimedId) ||
            !assertion.TryGetValue("openid.identity", out string? identity) ||
            !string.Equals(identity, claimedId, StringComparison.Ordinal) ||
            !SteamClaimedId().IsMatch(claimedId) ||
            !assertion.TryGetValue("openid.response_nonce", out string? responseNonce) ||
            string.IsNullOrWhiteSpace(responseNonce))
        {
            return SteamOpenIdVerification.Invalid;
        }

        var verification = assertion
            .Where(pair => pair.Key.StartsWith("openid.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        verification["openid.mode"] = "check_authentication";

        using var request = new HttpRequestMessage(HttpMethod.Post, ProviderEndpoint)
        {
            Content = new FormUrlEncodedContent(verification),
        };
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return SteamOpenIdVerification.Invalid;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        bool isValid = body
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), "is_valid:true", StringComparison.Ordinal));
        if (!isValid)
        {
            return SteamOpenIdVerification.Invalid;
        }

        string nonceHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(responseNonce)));
        if (!_acceptedResponseNonces.TryAdd(nonceHash, 0))
        {
            return SteamOpenIdVerification.Invalid;
        }

        _acceptedNonceOrder.Enqueue(nonceHash);
        while (_acceptedResponseNonces.Count > MaximumRememberedNonces &&
               _acceptedNonceOrder.TryDequeue(out string? expiredNonceHash))
        {
            _acceptedResponseNonces.TryRemove(expiredNonceHash, out _);
        }

        Match match = SteamClaimedId().Match(claimedId);
        return new SteamOpenIdVerification(true, match.Groups[1].Value);
    }

    private static bool HasValue(
        IReadOnlyDictionary<string, string> assertion,
        string key,
        string expected) =>
        assertion.TryGetValue(key, out string? actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    [GeneratedRegex("^https://steamcommunity\\.com/openid/id/(\\d{17})$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamClaimedId();
}

public sealed record SteamOpenIdVerification(bool Succeeded, string? SteamId64)
{
    public static SteamOpenIdVerification Invalid { get; } = new(false, null);
}
