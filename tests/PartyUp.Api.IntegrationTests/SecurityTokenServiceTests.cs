using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Auth;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SecurityTokenServiceTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Redeem_IsSingleUse_D067()
    {
        IssuedToken issued = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            null,
            "single-use@example.test",
            Duration.FromHours(1)));

        TokenRedemption first = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            issued.RawToken));
        TokenRedemption second = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            issued.RawToken));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(TokenRedemptionFailure.NotFound, second.Failure);
    }

    [Fact]
    public async Task Redeem_RefusesAnExpiredToken_D067()
    {
        IssuedToken issued = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            null,
            "expired@example.test",
            Duration.FromMinutes(1)));
        _factory.Clock.Advance(Duration.FromMinutes(2));

        TokenRedemption result = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            issued.RawToken));

        Assert.False(result.Succeeded);
        Assert.Equal(TokenRedemptionFailure.NotFound, result.Failure);
    }

    [Fact]
    public async Task Issue_DoesNotConsumeAnotherPartysOutstandingRecoveryToken_D067()
    {
        var request = new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            null,
            "recovery@example.test",
            Duration.FromHours(1));
        IssuedToken first = await IssueAsync(request);
        IssuedToken second = await IssueAsync(request);

        TokenRedemption firstResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            first.RawToken));
        TokenRedemption secondResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            second.RawToken));

        Assert.True(firstResult.Succeeded);
        Assert.True(secondResult.Succeeded);
    }

    [Fact]
    public async Task Redeem_RefusesATokenOfAnotherPurpose_D067()
    {
        IssuedToken magicLink = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.MagicLink,
            null,
            "purpose@example.test",
            Duration.FromMinutes(15)));

        TokenRedemption wrongRecoveryPurpose = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            magicLink.RawToken));
        TokenRedemption correctMagicPurpose = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            magicLink.RawToken));

        Assert.Equal(TokenRedemptionFailure.NotFound, wrongRecoveryPurpose.Failure);
        Assert.True(correctMagicPurpose.Succeeded);

        const string binderHash = "binder-hash";
        IssuedToken intent = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            null,
            null,
            Duration.FromMinutes(10),
            Intent: new ExternalIntentPayload(
                "google",
                ExternalStartMode.Login,
                "/app",
                false,
                binderHash)));

        TokenRedemption wrongDeletionPurpose = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            intent.RawToken));
        TokenRedemption correctIntentPurpose = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            intent.RawToken,
            binderHash));

        Assert.Equal(TokenRedemptionFailure.NotFound, wrongDeletionPurpose.Failure);
        Assert.True(correctIntentPurpose.Succeeded);
    }

    [Fact]
    public async Task Redeem_WithAMismatchedBinder_ConsumesNothing_D067()
    {
        const string binderHash = "base64url-binder-hash";
        IssuedToken issued = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            null,
            null,
            Duration.FromMinutes(10),
            Intent: new ExternalIntentPayload(
                "steam",
                ExternalStartMode.Login,
                "/app",
                false,
                binderHash)));

        TokenRedemption mismatch = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            issued.RawToken,
            "different-base64url-hash"));
        TokenRedemption retry = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            issued.RawToken,
            binderHash));

        Assert.Equal(TokenRedemptionFailure.NotFound, mismatch.Failure);
        Assert.True(retry.Succeeded);
        Assert.Equal(binderHash, retry.Intent?.BinderHash);
    }

    [Fact]
    public async Task Redeem_ForAPreAccountTokenWithoutAttestation_RefusesWithoutConsuming_D083()
    {
        IssuedToken magicLink = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.MagicLink,
            null,
            "age-gate@example.test",
            Duration.FromMinutes(15),
            new PendingRegistration("en", false)));

        TokenRedemption refused = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            magicLink.RawToken,
            AgeGateAttested: false));
        TokenRedemption retry = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            magicLink.RawToken,
            AgeGateAttested: true));

        Assert.Equal(TokenRedemptionFailure.RequiresAgeGate, refused.Failure);
        Assert.Equal("age-gate@example.test", refused.EmailNormalized);
        Assert.True(retry.Succeeded);

        const string binderHash = "returning-user-binder";
        IssuedToken intent = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            null,
            null,
            Duration.FromMinutes(10),
            Intent: new ExternalIntentPayload(
                "google",
                ExternalStartMode.Login,
                "/app",
                false,
                binderHash)));
        TokenRedemption intentResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            intent.RawToken,
            binderHash,
            AgeGateAttested: null));

        Assert.True(intentResult.Succeeded);
    }

    [Fact]
    public async Task Redeem_WithAnAttestation_ConsumesThePreAccountToken_D083()
    {
        IssuedToken callerAttested = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.MagicLink,
            null,
            "caller-attested@example.test",
            Duration.FromMinutes(15),
            new PendingRegistration("en", false)));

        Task<TokenRedemption> first = RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            callerAttested.RawToken,
            AgeGateAttested: true));
        Task<TokenRedemption> second = RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            callerAttested.RawToken,
            AgeGateAttested: true));
        TokenRedemption[] concurrent = await Task.WhenAll(first, second);

        Assert.Single(concurrent, result => result.Succeeded);
        Assert.Single(concurrent, result => !result.Succeeded);

        IssuedToken rowAttested = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.EmailVerification,
            null,
            "row-attested@example.test",
            Duration.FromHours(24),
            new PendingRegistration("pt-BR", true)));
        TokenRedemption rowAttestedResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.EmailVerification,
            rowAttested.RawToken,
            AgeGateAttested: false));

        Assert.True(rowAttestedResult.Succeeded);
        Assert.True(rowAttestedResult.Pending?.AgeGateAttested);
    }

    private async Task<IssuedToken> IssueAsync(IssueTokenRequest request)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        return await service.IssueAsync(request, default);
    }

    private async Task<TokenRedemption> RedeemAsync(RedeemTokenRequest request)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        return await service.RedeemAsync(request, default);
    }
}