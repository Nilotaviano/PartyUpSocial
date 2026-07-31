using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class ListAuthMethodsUseCaseTests
{
    [Fact]
    public async Task Execute_ReportsEveryLinkedMethod_D087()
    {
        var accounts = new FakeAccountGateway();
        UserId id = UserId.New();
        accounts.PasswordUsers.Add(id);
        accounts.Emails[id] = new AccountEmailInfo("Player@Example.test", true);
        accounts.LoginProviders[id] = [ExternalProviderPolicy.Google, ExternalProviderPolicy.Steam];

        AuthMethodsResult result = await new ListAuthMethodsUseCase(accounts)
            .ExecuteAsync(id, default);

        Assert.Equal(["password", "magicLink", "google", "steam"],
            result.Methods.Where(method => method.Linked).Select(method => method.Kind));
    }

    [Fact]
    public async Task Execute_ReportsMagicLinkForAConfirmedAddress_D067()
    {
        var accounts = new FakeAccountGateway();
        UserId id = UserId.New();
        accounts.Emails[id] = new AccountEmailInfo("Player@Example.test", true);

        AuthMethodsResult result = await new ListAuthMethodsUseCase(accounts)
            .ExecuteAsync(id, default);

        AuthMethod magicLink = Assert.Single(
            result.Methods,
            method => method.Kind == "magicLink");
        Assert.True(magicLink.Linked);
        Assert.False(magicLink.Removable);
        Assert.Equal("P***@Example.test", magicLink.Detail);
    }
}

public sealed class UnlinkAuthMethodUseCaseTests
{
    [Fact]
    public async Task Execute_CountsMagicLinkTowardTheLastMethodRule_D137()
    {
        TestContext context = Create(confirmedEmail: true);
        context.Accounts.PasswordUsers.Add(context.User.Id);

        UnlinkAuthMethodResult result = await context.UseCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(context.User.Id, "password", true),
            default);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(context.User.Id, context.Accounts.PasswordUsers);
    }

    [Fact]
    public async Task Execute_RefusesToRemoveMagicLinkDirectly_D137()
    {
        TestContext context = Create(confirmedEmail: true);

        UnlinkAuthMethodResult result = await context.UseCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(context.User.Id, "magicLink", true),
            default);

        Assert.Equal("method_not_removable", result.Code);
        Assert.Equal(0, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_RemovesANonFinalMethod_D087()
    {
        TestContext context = Create(confirmedEmail: false);
        context.Accounts.PasswordUsers.Add(context.User.Id);
        context.Accounts.LoginProviders[context.User.Id] = [ExternalProviderPolicy.Google];

        UnlinkAuthMethodResult result = await context.UseCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(context.User.Id, "google", true),
            default);

        Assert.True(result.Succeeded);
        Assert.Contains(
            (context.User.Id, ExternalProviderPolicy.Google),
            context.Accounts.RemovedLogins);
        Assert.Equal(1, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_RefusesToUnlinkTheLastMethod_D137()
    {
        TestContext context = Create(confirmedEmail: false);
        context.Accounts.PasswordUsers.Add(context.User.Id);

        UnlinkAuthMethodResult result = await context.UseCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(context.User.Id, "password", true),
            default);

        Assert.Equal("last_auth_method", result.Code);
        Assert.Contains(context.User.Id, context.Accounts.PasswordUsers);
    }

    [Fact]
    public async Task Execute_RequiresReauthentication_D137()
    {
        TestContext context = Create(confirmedEmail: false);
        context.Accounts.PasswordUsers.Add(context.User.Id);

        UnlinkAuthMethodResult result = await context.UseCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(context.User.Id, "password", false),
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.Equal(0, context.UnitOfWork.Executions);
    }

    [Fact]
    public async Task Execute_NotifiesTheAccountAddress_D137()
    {
        TestContext context = Create(confirmedEmail: true);
        context.Accounts.PasswordUsers.Add(context.User.Id);
        context.Accounts.LoginProviders[context.User.Id] = [ExternalProviderPolicy.Google];

        await context.UseCase.ExecuteAsync(
            new UnlinkAuthMethodCommand(context.User.Id, "google", true),
            default);

        EmailMessage message = Assert.Single(context.Email.Messages);
        Assert.Equal("AsTyped@Example.test", message.ToAddress);
        Assert.Equal(EmailTemplate.AuthMethodRemoved, message.Template);
        Assert.Equal(1, context.Sessions.Rotations);
    }

    private static TestContext Create(bool confirmedEmail)
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        accounts.Emails[user.Id] = confirmedEmail
            ? new AccountEmailInfo("AsTyped@Example.test", true)
            : null;
        var sessions = new FakeAccountSessionService();
        var unitOfWork = new FakeUnitOfWork();
        var email = new FakeEmailDispatcher();
        var unlinkSteam = new UnlinkSteamUseCase(
            users,
            accounts,
            sessions,
            unitOfWork,
            email);
        var useCase = new UnlinkAuthMethodUseCase(
            users,
            accounts,
            sessions,
            unitOfWork,
            email,
            unlinkSteam);
        return new TestContext(useCase, user, accounts, sessions, unitOfWork, email);
    }

    private sealed record TestContext(
        UnlinkAuthMethodUseCase UseCase,
        User User,
        FakeAccountGateway Accounts,
        FakeAccountSessionService Sessions,
        FakeUnitOfWork UnitOfWork,
        FakeEmailDispatcher Email);
}
