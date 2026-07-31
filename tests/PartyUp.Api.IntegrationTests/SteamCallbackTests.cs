using System.Net;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SteamCallbackTests
{
    private const string SteamId = "76561198000000000";
    private const string ReturnTo = "https://partyup.social/api/v1/auth/external/steam/callback?n=nonce";

    [Fact]
    public async Task Callback_WithAForgedAssertion_IsRejected_D067()
    {
        var verifier = CreateVerifier("is_valid:false");

        SteamOpenIdVerification result = await verifier.VerifyAsync(Assertion(), ReturnTo, default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Callback_WithClaimedIdStrippedFromOpenIdSigned_IsRejected_D067()
    {
        var handler = new StubHandler("is_valid:true");
        var verifier = new SteamOpenIdVerifier(new HttpClient(handler));
        Dictionary<string, string> assertion = Assertion();
        assertion["openid.signed"] = "return_to,response_nonce,assoc_handle,op_endpoint";

        SteamOpenIdVerification result = await verifier.VerifyAsync(assertion, ReturnTo, default);

        Assert.False(result.Succeeded);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Callback_WithAWrongOpenIdMode_IsRejected()
    {
        var verifier = CreateVerifier("is_valid:true");
        Dictionary<string, string> assertion = Assertion();
        assertion["openid.mode"] = "checkid_setup";

        SteamOpenIdVerification result = await verifier.VerifyAsync(assertion, ReturnTo, default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Callback_WithAMismatchedReturnTo_IsRejected()
    {
        var verifier = CreateVerifier("is_valid:true");

        SteamOpenIdVerification result = await verifier.VerifyAsync(
            Assertion(),
            ReturnTo + "-other",
            default);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Callback_ReplayedResponseNonce_IsRejected_D067()
    {
        var verifier = CreateVerifier("is_valid:true");

        SteamOpenIdVerification first = await verifier.VerifyAsync(Assertion(), ReturnTo, default);
        SteamOpenIdVerification replay = await verifier.VerifyAsync(Assertion(), ReturnTo, default);

        Assert.True(first.Succeeded);
        Assert.False(replay.Succeeded);
    }

    private static SteamOpenIdVerifier CreateVerifier(string response) =>
        new(new HttpClient(new StubHandler(response)));

    private static Dictionary<string, string> Assertion()
    {
        string claimedId = $"https://steamcommunity.com/openid/id/{SteamId}";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openid.ns"] = SteamOpenIdVerifier.OpenIdNamespace,
            ["openid.mode"] = "id_res",
            ["openid.op_endpoint"] = SteamOpenIdVerifier.ProviderEndpoint,
            ["openid.claimed_id"] = claimedId,
            ["openid.identity"] = claimedId,
            ["openid.return_to"] = ReturnTo,
            ["openid.response_nonce"] = "2026-07-31T12:00:00Znonce",
            ["openid.assoc_handle"] = "handle",
            ["openid.signed"] = "claimed_id,identity,return_to,response_nonce,assoc_handle,op_endpoint",
            ["openid.sig"] = "signature",
        };
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            });
        }
    }
}
