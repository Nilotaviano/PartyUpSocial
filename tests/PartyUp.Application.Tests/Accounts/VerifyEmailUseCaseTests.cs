using NodaTime;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class VerifyEmailUseCaseTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 31, 12, 0);

    [Fact]
    public async Task Execute_CreatesTheAccountFromAPendingRegistration_D016()
    {
        TestContext context = Create();

        EmailVerificationResult result = await context.UseCase.ExecuteAsync(
            "token",
            "redeemer-password",
            true,
            default);

        Assert.True(result.ShouldSignIn);
        Assert.Single(context.Users.Added);
        Assert.Single(context.Accounts.Created);
    }

    [Fact]
    public async Task Execute_TakesThePasswordFromTheRedeemerNotTheRequester_D016()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            "token",
            "mailbox-owner-password",
            true,
            default);

        Assert.Equal("mailbox-owner-password", context.Accounts.Created[0].Password);
        Assert.DoesNotContain(
            context.Tokens.Issues,
            issue => issue.Pending is not null &&
                issue.Pending.ToString()!.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            "token",
            "mailbox-owner-password",
            true,
            default);

        User user = Assert.Single(context.Users.Added);
        Assert.Equal(AgeGateSource.SelfAttested, user.AgeGateSource);
        Assert.True(user.AgeGatePassed);
    }

    [Fact]
    public async Task Execute_PreservesTheMailboxAddressCaseWhenCreatingTheAccount_D016()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            "token",
            "mailbox-owner-password",
            true,
            default);

        Assert.Equal("Player@corp.example", Assert.Single(context.Accounts.Created).Email);
    }

    [Fact]
    public async Task Execute_UnattestedAgeGateReturnsAgeGateRequired_D083()
    {
        TestContext context = Create(
            Failed(TokenRedemptionFailure.RequiresAgeGate),
            Success());

        EmailVerificationResult refused = await context.UseCase.ExecuteAsync(
            "same-token",
            "mailbox-owner-password",
            false,
            default);
        EmailVerificationResult retried = await context.UseCase.ExecuteAsync(
            "same-token",
            "mailbox-owner-password",
            true,
            default);

        Assert.Equal("age_gate_required", refused.Code);
        Assert.True(retried.Succeeded);
        Assert.Equal(["same-token", "same-token"], context.Tokens.Redeems.Select(x => x.RawToken));
    }

    [Fact]
    public async Task Execute_AttestedRetryWithTheSameTokenSucceeds_D083()
    {
        TestContext context = Create(
            Failed(TokenRedemptionFailure.RequiresAgeGate),
            Success());

        await context.UseCase.ExecuteAsync(
            "same-token",
            "mailbox-owner-password",
            false,
            default);
        EmailVerificationResult result = await context.UseCase.ExecuteAsync(
            "same-token",
            "mailbox-owner-password",
            true,
            default);

        Assert.True(result.Succeeded);
        Assert.False(context.Tokens.Redeems[0].AgeGateAttested);
        Assert.True(context.Tokens.Redeems[1].AgeGateAttested);
    }

    private static TestContext Create(params TokenRedemption[] redemptions)
    {
        var accounts = new FakeAccountGateway();
        var users = new FakeUserRepository();
        var tokens = new FakeTokenService();
        foreach (TokenRedemption redemption in redemptions.Length == 0
                     ? [Success()]
                     : redemptions)
        {
            tokens.RedemptionResults.Enqueue(redemption);
        }
        var useCase = new VerifyEmailUseCase(
            accounts,
            users,
            new FakeFriendCodeGenerator(),
            tokens,
            new FakeUnitOfWork(),
            new TestClock(Now));
        return new TestContext(useCase, accounts, users, tokens);
    }

    private static TokenRedemption Success() =>
        new(
            true,
            null,
            AuthTokenPurpose.EmailVerification,
            null,
            "PLAYER@EXAMPLE.TEST",
            new PendingRegistration("en", true),
            null,
            Now,
            "Player@corp.example");

    private static TokenRedemption Failed(TokenRedemptionFailure failure) =>
        new(
            false,
            failure,
            AuthTokenPurpose.EmailVerification,
            null,
            "PLAYER@EXAMPLE.TEST",
            new PendingRegistration("en", false),
            null,
            default,
            "Player@corp.example");

    private sealed record TestContext(
        VerifyEmailUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeUserRepository Users,
        FakeTokenService Tokens);
}
