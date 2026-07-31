using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Accounts;

public sealed class ExternalIntentUseCaseTests
{
    [Fact]
    public async Task Execute_RequiresReauthenticationInLinkMode_D087()
    {
        TestContext context = Create(validProof: false);

        ExternalIntentIssueResult result = await context.UseCase.ExecuteAsync(
            Command(ExternalStartMode.Link, context.UserId, "/app"),
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.Empty(context.Tokens.Issues);
    }

    [Fact]
    public async Task Execute_PreservesTheCallerSuppliedReturnTo()
    {
        TestContext context = Create(validProof: true);

        await context.UseCase.ExecuteAsync(
            Command(ExternalStartMode.Login, null, "/app"),
            default);

        Assert.Equal("/app", Assert.Single(context.Tokens.Issues).Intent!.ReturnTo);
    }

    [Fact]
    public async Task Execute_LoginIntentFromAnAuthenticatedCallerHasNoBoundUser()
    {
        TestContext context = Create(validProof: true);

        await context.UseCase.ExecuteAsync(
            Command(ExternalStartMode.Login, context.UserId, "/app"),
            default);

        Assert.Null(Assert.Single(context.Tokens.Issues).UserId);
    }

    private static IssueExternalIntentCommand Command(
        ExternalStartMode mode,
        UserId? callerId,
        string returnTo) =>
        new(
            ExternalProviderPolicy.Google,
            mode,
            returnTo,
            true,
            "binder-hash",
            callerId,
            mode == ExternalStartMode.Link ? "current-password" : null,
            null);

    private static TestContext Create(bool validProof)
    {
        UserId id = UserId.New();
        var accounts = new FakeAccountGateway();
        accounts.PasswordChecks[id] = new PasswordCheckResult(validProof, false, true);
        var tokens = new FakeTokenService();
        var reauthenticate = new ReauthenticateUseCase(
            accounts,
            new FakeUserRepository(),
            tokens,
            new FakeUnitOfWork(),
            new FakeEmailDispatcher(),
            new AccountEmailOptions("https://partyup.social"));
        var useCase = new IssueExternalIntentUseCase(
            reauthenticate,
            tokens,
            new FakeUnitOfWork());
        return new TestContext(useCase, tokens, id);
    }

    private sealed record TestContext(
        IssueExternalIntentUseCase UseCase,
        FakeTokenService Tokens,
        UserId UserId);
}
