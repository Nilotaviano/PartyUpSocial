using NodaTime;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class ConsumeMagicLinkUseCaseTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 7, 31, 12, 0);

    [Fact]
    public async Task Execute_SignsInWithoutAPassword_D067()
    {
        TestContext context = Create(Success(null, "NEW@EXAMPLE.TEST"));

        MagicLinkConsumptionResult result = await context.UseCase.ExecuteAsync(
            "token",
            true,
            default);

        Assert.True(result.Succeeded);
        Assert.Null(context.Accounts.Created[0].Password);
    }

    [Fact]
    public async Task Execute_PreservesTheMailboxAddressCaseWhenCreatingTheAccount()
    {
        TestContext context = Create(
            Success(null, "PLAYER@CORP.EXAMPLE", "Player@corp.example"));

        await context.UseCase.ExecuteAsync("token", true, default);

        Assert.Equal("Player@corp.example", Assert.Single(context.Accounts.Created).Email);
    }

    [Fact]
    public async Task Execute_RejectsAReusedToken_D067()
    {
        UserId id = UserId.New();
        TestContext context = Create(
            Success(id, "PLAYER@EXAMPLE.TEST"),
            Failed(TokenRedemptionFailure.AlreadyConsumed));

        MagicLinkConsumptionResult first = await context.UseCase.ExecuteAsync(
            "token",
            false,
            default);
        MagicLinkConsumptionResult second = await context.UseCase.ExecuteAsync(
            "token",
            false,
            default);

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Execute_AdoptsAnAccountCreatedAfterTheTokenWasIssued_D067()
    {
        UserId appeared = UserId.New();
        TestContext context = Create(Success(null, "PLAYER@EXAMPLE.TEST"));
        context.Accounts.EmailUsers["PLAYER@EXAMPLE.TEST"] = appeared;

        MagicLinkConsumptionResult result = await context.UseCase.ExecuteAsync(
            "token",
            false,
            default);

        Assert.Equal(appeared, result.UserId);
        Assert.Empty(context.Users.Added);
    }

    [Fact]
    public async Task Execute_RejectsAnUnattestedAgeGateOnAccountCreation_D083()
    {
        TestContext context = Create(Failed(TokenRedemptionFailure.RequiresAgeGate));

        MagicLinkConsumptionResult result = await context.UseCase.ExecuteAsync(
            "token",
            false,
            default);

        Assert.Equal("age_gate_required", result.Code);
        Assert.Empty(context.Users.Added);
        Assert.False(context.Tokens.Redeems[0].AgeGateAttested);
    }

    [Fact]
    public async Task Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093()
    {
        TestContext context = Create(Success(null, "NEW@EXAMPLE.TEST"));

        await context.UseCase.ExecuteAsync("token", true, default);

        Assert.Equal(AgeGateSource.SelfAttested, context.Users.Added[0].AgeGateSource);
    }

    [Fact]
    public async Task Execute_SkipsTheAgeGateWhenAdoptingAnExistingAccount_D083()
    {
        UserId id = UserId.New();
        TestContext context = Create(Success(null, "PLAYER@EXAMPLE.TEST"));
        context.Accounts.EmailUsers["PLAYER@EXAMPLE.TEST"] = id;

        MagicLinkConsumptionResult result = await context.UseCase.ExecuteAsync(
            "token",
            false,
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(id, result.UserId);
    }

    private static TestContext Create(params TokenRedemption[] redemptions)
    {
        var accounts = new FakeAccountGateway();
        var users = new FakeUserRepository();
        var tokens = new FakeTokenService();
        foreach (TokenRedemption redemption in redemptions)
        {
            tokens.RedemptionResults.Enqueue(redemption);
        }

        var useCase = new ConsumeMagicLinkUseCase(
            accounts,
            users,
            new FakeFriendCodeGenerator(),
            tokens,
            new FakeUnitOfWork(),
            new TestClock(Now));
        return new TestContext(useCase, accounts, users, tokens);
    }

    private static TokenRedemption Success(
        UserId? id,
        string emailNormalized,
        string? email = null) =>
        new(
            true,
            null,
            AuthTokenPurpose.MagicLink,
            id,
            emailNormalized,
            new PendingRegistration("en", false),
            null,
            Now,
            email);

    private static TokenRedemption Failed(TokenRedemptionFailure failure) =>
        new(
            false,
            failure,
            AuthTokenPurpose.MagicLink,
            null,
            "NEW@EXAMPLE.TEST",
            new PendingRegistration("en", false),
            null,
            default);

    private sealed record TestContext(
        ConsumeMagicLinkUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeUserRepository Users,
        FakeTokenService Tokens);
}

public sealed class RequestMagicLinkUseCaseTests
{
    [Fact]
    public async Task Execute_PreservesTheMailboxAddressCaseInTheTokenAndMessage()
    {
        var accounts = new FakeAccountGateway();
        var tokens = new FakeTokenService();
        var email = new FakeEmailDispatcher();
        var useCase = new RequestMagicLinkUseCase(
            accounts,
            tokens,
            new FakeUnitOfWork(),
            email,
            new AccountEmailOptions("https://partyup.social"));

        await useCase.ExecuteAsync("Player@corp.example", "en", default);

        IssueTokenRequest issue = Assert.Single(tokens.Issues);
        Assert.Equal("PLAYER@CORP.EXAMPLE", issue.EmailNormalized);
        Assert.Equal("Player@corp.example", issue.Email);
        Assert.Equal("Player@corp.example", Assert.Single(email.Messages).ToAddress);
    }
}
