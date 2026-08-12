using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Persistence;

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

    [Theory]
    [InlineData("/u/player.name")]
    [InlineData("/add/23456789AB")]
    public async Task EmailTokenPayload_RoundTripsCanonicalFriendContinuation(
        string continuation)
    {
        IssuedToken issued = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.MagicLink,
            null,
            "CONTINUATION@EXAMPLE.TEST",
            Duration.FromMinutes(15),
            Email: "Continuation@Example.Test",
            Continuation: continuation));

        TokenRedemption redemption = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.MagicLink,
            issued.RawToken));

        Assert.True(redemption.Succeeded);
        Assert.Equal("Continuation@Example.Test", redemption.Email);
        Assert.Equal(continuation, redemption.Continuation);
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
    public async Task Issue_DoesNotConsumeAnotherPartysOutstandingDeletionConfirmation_D072()
    {
        UserId userId = await _factory.SeedUserAsync(email: "deletion-token@example.test");
        IssuedToken emailLeg = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            userId,
            "DELETION-TOKEN@EXAMPLE.TEST",
            Duration.FromHours(1)));
        IssuedToken repeatedRequest = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            userId,
            "DELETION-TOKEN@EXAMPLE.TEST",
            Duration.FromHours(1)));
        IssuedToken steamLeg = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            userId,
            null,
            Duration.FromMinutes(10)));

        TokenRedemption first = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            emailLeg.RawToken));
        TokenRedemption second = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            repeatedRequest.RawToken));
        TokenRedemption third = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.DeletionRequestConfirmation,
            steamLeg.RawToken));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(third.Succeeded);
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
    public async Task Redeem_ExternalIntentReturnsOnlyTheIntentPayload_D067()
    {
        const string binderHash = "external-intent-payload-binder";
        var expected = new ExternalIntentPayload(
            "google",
            ExternalStartMode.Login,
            "/settings/account",
            true,
            binderHash);
        IssuedToken issued = await IssueAsync(new IssueTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            null,
            null,
            Duration.FromMinutes(10),
            Intent: expected));

        TokenRedemption redemption = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.ExternalStartIntent,
            issued.RawToken,
            binderHash));

        Assert.True(redemption.Succeeded);
        Assert.Equal(expected, redemption.Intent);
        Assert.Null(redemption.Email);
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

    [Fact]
    public async Task Redeem_TwoConcurrentRequestsHaveExactlyOneWinner_D067()
    {
        UserId userId = await _factory.SeedUserAsync(
            email: "token-race@example.test",
            password: "correct-password");
        AuthTokenPurpose[] purposes = Enum.GetValues<AuthTokenPurpose>();

        foreach (AuthTokenPurpose purpose in purposes)
        {
            IssuedToken issued = await IssueAsync(CreateIssueRequest(purpose, userId));
            var barrier = new AsyncBarrier(2);

            Task<TokenRedemption>[] attempts =
            [
                RedeemAfterBarrierAsync(CreateRedeemRequest(purpose, issued.RawToken), barrier),
                RedeemAfterBarrierAsync(CreateRedeemRequest(purpose, issued.RawToken), barrier),
            ];
            TokenRedemption[] results = await Task.WhenAll(attempts);

            Assert.Single(results, result => result.Succeeded);
            TokenRedemption loser = Assert.Single(results, result => !result.Succeeded);
            Assert.Equal(TokenRedemptionFailure.NotFound, loser.Failure);
            Instant consumedAt = await ConsumedAtAsync(issued.TokenId);
            _factory.Clock.Advance(Duration.FromSeconds(1));
            Assert.Equal(
                TokenRedemptionFailure.NotFound,
                (await RedeemAsync(CreateRedeemRequest(purpose, issued.RawToken))).Failure);
            Assert.Equal(consumedAt, await ConsumedAtAsync(issued.TokenId));
        }
    }

    [Fact]
    public async Task Issue_TwoConsecutiveEmailUnsubscribeTokensAreBothLive_D189()
    {
        UserId userId = await _factory.SeedUserAsync(email: "unsubscribe-issue@example.test");
        var request = new IssueTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            userId,
            null,
            Duration.FromDays(365));

        IssuedToken first = await IssueAsync(request);
        IssuedToken second = await IssueAsync(request);

        TokenRedemption firstResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            first.RawToken));
        TokenRedemption secondResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            second.RawToken));

        Assert.True(firstResult.Succeeded);
        Assert.True(secondResult.Succeeded);
    }

    [Fact]
    public async Task Redeem_ConsumesEveryOutstandingEmailUnsubscribeTokenForTheUser_D189()
    {
        UserId userId = await _factory.SeedUserAsync(email: "unsubscribe-redeem@example.test");
        var request = new IssueTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            userId,
            null,
            Duration.FromDays(365));

        IssuedToken first = await IssueAsync(request);
        IssuedToken second = await IssueAsync(request);
        IssuedToken third = await IssueAsync(request);

        TokenRedemption redeemedFirst = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            first.RawToken));
        TokenRedemption secondAttempt = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            second.RawToken));
        TokenRedemption thirdAttempt = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.EmailUnsubscribe,
            third.RawToken));

        Assert.True(redeemedFirst.Succeeded);
        Assert.False(secondAttempt.Succeeded);
        Assert.Equal(TokenRedemptionFailure.NotFound, secondAttempt.Failure);
        Assert.False(thirdAttempt.Succeeded);
        Assert.Equal(TokenRedemptionFailure.NotFound, thirdAttempt.Failure);
    }

    [Fact]
    public async Task Redeem_ExistingPurposesAreUnaffectedByTheUnsubscribeSiblingConsume_D189()
    {
        UserId userId = await _factory.SeedUserAsync(email: "unsubscribe-unaffected@example.test");
        var passwordResetRequest = new IssueTokenRequest(
            AuthTokenPurpose.PasswordReset,
            userId,
            "UNSUBSCRIBE-UNAFFECTED@EXAMPLE.TEST",
            Duration.FromHours(1),
            Email: "unsubscribe-unaffected@example.test");

        IssuedToken firstReset = await IssueAsync(passwordResetRequest);
        IssuedToken secondReset = await IssueAsync(passwordResetRequest);

        TokenRedemption firstResetResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            firstReset.RawToken));
        TokenRedemption secondResetResult = await RedeemAsync(new RedeemTokenRequest(
            AuthTokenPurpose.PasswordReset,
            secondReset.RawToken));

        Assert.True(firstResetResult.Succeeded);
        Assert.True(secondResetResult.Succeeded);
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

    private async Task<TokenRedemption> RedeemAfterBarrierAsync(
        RedeemTokenRequest request,
        AsyncBarrier barrier)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
        await barrier.SignalAndWaitAsync();
        return await service.RedeemAsync(request, default);
    }

    private static IssueTokenRequest CreateIssueRequest(
        AuthTokenPurpose purpose,
        UserId userId) =>
        purpose switch
        {
            AuthTokenPurpose.MagicLink => new(
                purpose,
                userId,
                "TOKEN-RACE@EXAMPLE.TEST",
                Duration.FromMinutes(15),
                Email: "token-race@example.test"),
            AuthTokenPurpose.PasswordReset => new(
                purpose,
                userId,
                "TOKEN-RACE@EXAMPLE.TEST",
                Duration.FromHours(1),
                Email: "token-race@example.test"),
            AuthTokenPurpose.EmailVerification => new(
                purpose,
                userId,
                "TOKEN-RACE@EXAMPLE.TEST",
                Duration.FromHours(24),
                Email: "token-race@example.test"),
            AuthTokenPurpose.EmailChange => new(
                purpose,
                userId,
                "CHANGED-TOKEN-RACE@EXAMPLE.TEST",
                Duration.FromHours(1),
                Email: "changed-token-race@example.test"),
            AuthTokenPurpose.DeletionRequestConfirmation => new(
                purpose,
                userId,
                "TOKEN-RACE@EXAMPLE.TEST",
                Duration.FromHours(1)),
            AuthTokenPurpose.ExternalStartIntent => new(
                purpose,
                null,
                null,
                Duration.FromMinutes(10),
                Intent: new ExternalIntentPayload(
                    "google",
                    ExternalStartMode.Login,
                    "/app",
                    false,
                    "race-binder-hash")),
            AuthTokenPurpose.Reauthentication => new(
                purpose,
                userId,
                null,
                Duration.FromMinutes(5)),
            AuthTokenPurpose.EmailUnsubscribe => new(
                purpose,
                userId,
                null,
                Duration.FromDays(365)),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
        };

    private static RedeemTokenRequest CreateRedeemRequest(
        AuthTokenPurpose purpose,
        string rawToken) =>
        new(
            purpose,
            rawToken,
            BinderHash: purpose == AuthTokenPurpose.ExternalStartIntent
                ? "race-binder-hash"
                : null,
            AgeGateAttested: purpose is
                AuthTokenPurpose.MagicLink or AuthTokenPurpose.EmailVerification
                    ? true
                    : null);

    private async Task<Instant> ConsumedAtAsync(Guid tokenId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Database.SqlQueryRaw<Instant>(
                """
                SELECT consumed_at AS "Value"
                  FROM auth_tokens
                 WHERE id = {0}
                """,
                tokenId)
            .SingleAsync();
    }

    private sealed class AsyncBarrier(int participants)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        internal Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) == participants)
            {
                _release.TrySetResult();
            }

            return _release.Task;
        }
    }
}
