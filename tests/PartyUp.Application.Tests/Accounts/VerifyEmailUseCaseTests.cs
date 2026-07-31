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
            default);

        Assert.True(result.ShouldSignIn);
        Assert.Single(context.Users.Added);
        Assert.Single(context.Accounts.Created);
    }

    [Fact]
    public async Task Execute_TakesThePasswordFromTheRedeemerNotTheRequester_D016()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync("token", "mailbox-owner-password", default);

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

        await context.UseCase.ExecuteAsync("token", "mailbox-owner-password", default);

        User user = Assert.Single(context.Users.Added);
        Assert.Equal(AgeGateSource.SelfAttested, user.AgeGateSource);
        Assert.True(user.AgeGatePassed);
    }

    private static TestContext Create()
    {
        var accounts = new FakeAccountGateway();
        var users = new FakeUserRepository();
        var tokens = new FakeTokenService();
        tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.EmailVerification,
            null,
            "PLAYER@EXAMPLE.TEST",
            new PendingRegistration("en", true),
            null,
            Now));
        var useCase = new VerifyEmailUseCase(
            accounts,
            users,
            new FakeFriendCodeGenerator(),
            tokens,
            new FakeUnitOfWork(),
            new TestClock(Now));
        return new TestContext(useCase, accounts, users, tokens);
    }

    private sealed record TestContext(
        VerifyEmailUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeUserRepository Users,
        FakeTokenService Tokens);
}
