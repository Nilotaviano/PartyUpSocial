using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Accounts;

public sealed class RegisterWithPasswordUseCaseTests
{
    [Fact]
    public async Task Execute_AcceptsARegistrationRequestFromAnyone_D016()
    {
        TestContext context = Create();

        RegistrationResult result = await context.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("new@example.test", true, "en"),
            default);

        Assert.True(result.IsAccepted);
        Assert.Single(context.Tokens.Issues);
    }

    [Fact]
    public async Task Execute_PreservesTheMailboxAddressCaseInTheTokenAndMessage_D016()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("Player@corp.example", true, "en"),
            default);

        IssueTokenRequest issue = Assert.Single(context.Tokens.Issues);
        Assert.Equal("PLAYER@CORP.EXAMPLE", issue.EmailNormalized);
        Assert.Equal("Player@corp.example", issue.Email);
        Assert.Equal(
            "Player@corp.example",
            Assert.Single(context.Email.Messages).ToAddress);
    }

    [Fact]
    public async Task Execute_RejectsAnUnattestedAgeGate_D083()
    {
        TestContext context = Create();

        RegistrationResult result = await context.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("new@example.test", false, "en"),
            default);

        Assert.False(result.IsAccepted);
        Assert.Equal("age_gate_required", result.Code);
        Assert.Empty(context.Tokens.Issues);
        Assert.Empty(context.Email.Messages);
    }

    [Fact]
    public async Task Execute_DoesNotRevealAnExistingAccount_D016()
    {
        TestContext unknown = Create();
        TestContext known = Create();
        known.Accounts.EmailUsers["known@example.test"] = UserId.New();

        RegistrationResult first = await unknown.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("unknown@example.test", true, "en"),
            default);
        RegistrationResult second = await known.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("known@example.test", true, "en"),
            default);

        Assert.Equal(first, second);
        Assert.Empty(unknown.Accounts.Created);
        Assert.Empty(known.Accounts.Created);
    }

    [Fact]
    public async Task Execute_CreatesNoAccountUntilTheMailboxIsProven_D016()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("new@example.test", true, "en"),
            default);

        Assert.Empty(context.Accounts.Created);
    }

    [Fact]
    public async Task Execute_IssuesTheSameWorkOnBothBranches_D016()
    {
        TestContext unknown = Create();
        TestContext known = Create();
        known.Accounts.EmailUsers["known@example.test"] = UserId.New();

        await unknown.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("unknown@example.test", true, "en"),
            default);
        await known.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand("known@example.test", true, "en"),
            default);

        Assert.Single(unknown.Tokens.Issues);
        Assert.Single(known.Tokens.Issues);
        Assert.Single(unknown.Email.Messages);
        Assert.Single(known.Email.Messages);
        Assert.Equal(EmailTemplate.EmailVerification, unknown.Email.Messages[0].Template);
        Assert.Equal(EmailTemplate.ExistingAccountNotice, known.Email.Messages[0].Template);
    }

    [Theory]
    [InlineData("/u/Player.One", "/u/player.one")]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB")]
    public async Task Execute_IssuesAndEmailsOnlyServerCanonicalContinuation_D031(
        string candidate,
        string expected)
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand(
                "new@example.test",
                true,
                "en",
                candidate),
            default);

        Assert.Equal(expected, Assert.Single(context.Tokens.Issues).Continuation);
        string fragment = Assert.Single(context.Email.Messages).Model["Continuation"];
        Assert.Equal($"&next={Uri.EscapeDataString(expected)}", fragment);
    }

    [Fact]
    public async Task Execute_DropsAnInvalidContinuationBeforeTokenOrEmail_D031()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            new RegisterWithPasswordCommand(
                "new@example.test",
                true,
                "en",
                "https://attacker.example/u/player"),
            default);

        Assert.Null(Assert.Single(context.Tokens.Issues).Continuation);
        Assert.Equal(string.Empty, Assert.Single(context.Email.Messages).Model["Continuation"]);
    }

    private static TestContext Create()
    {
        var accounts = new FakeAccountGateway();
        var tokens = new FakeTokenService();
        var email = new FakeEmailDispatcher();
        var useCase = new RegisterWithPasswordUseCase(
            accounts,
            tokens,
            new FakeUnitOfWork(),
            email,
            new AccountEmailOptions("https://partyup.social"));
        return new TestContext(useCase, accounts, tokens, email);
    }

    private sealed record TestContext(
        RegisterWithPasswordUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeTokenService Tokens,
        FakeEmailDispatcher Email);
}
