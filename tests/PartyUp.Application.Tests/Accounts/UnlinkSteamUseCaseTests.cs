using NodaTime;
using PartyUp.Application.Accounts;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class UnlinkSteamUseCaseTests
{
    [Theory]
    [InlineData(UnlinkOrigin.AuthMethod)]
    [InlineData(UnlinkOrigin.PlatformLink)]
    public async Task Execute_ByEitherRoute_RemovesTheLoginTheLinkAndTheSteamIdentity_D032(
        UnlinkOrigin origin)
    {
        TestContext context = Create();
        context.Accounts.LoginProviders[context.User.Id] = ["steam", "google"];

        UnlinkSteamResult result = await context.UseCase.ExecuteAsync(
            new UnlinkSteamCommand(
                context.User.Id,
                origin,
                true,
                origin == UnlinkOrigin.PlatformLink ? context.Link.Id : null),
            default);

        Assert.True(result.Succeeded);
        Assert.Empty(context.User.PlatformLinks);
        Assert.Contains(
            context.Accounts.RemovedLogins,
            value => value == (context.User.Id, ExternalProviderPolicy.Steam));
        Assert.Contains(context.User.Id, context.Accounts.RemovedSteamIdentities);
        Assert.Equal(1, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_ForASteamLinkRequestedAsAPlatformUnlink_StillRefusesTheLastAuthMethod_D137()
    {
        TestContext context = Create();
        context.Accounts.LoginProviders[context.User.Id] = ["steam"];

        UnlinkSteamResult result = await context.UseCase.ExecuteAsync(
            new UnlinkSteamCommand(
                context.User.Id,
                UnlinkOrigin.PlatformLink,
                true,
                context.Link.Id),
            default);

        Assert.Equal("last_auth_method", result.Code);
        Assert.Single(context.User.PlatformLinks);
        Assert.Empty(context.Accounts.RemovedLogins);
        Assert.Empty(context.Accounts.RemovedSteamIdentities);
    }

    [Fact]
    public async Task Execute_ForASteamLinkRequestedAsAPlatformUnlink_RequiresReauthentication_D137()
    {
        TestContext context = Create();

        UnlinkSteamResult result = await context.UseCase.ExecuteAsync(
            new UnlinkSteamCommand(
                context.User.Id,
                UnlinkOrigin.PlatformLink,
                false,
                context.Link.Id),
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.Equal(0, context.UnitOfWork.Executions);
        Assert.Single(context.User.PlatformLinks);
    }

    [Fact]
    public async Task Execute_NotifiesTheAccountAddress_D137()
    {
        TestContext context = Create();
        context.Accounts.LoginProviders[context.User.Id] = ["steam", "google"];
        context.Accounts.Emails[context.User.Id] =
            new AccountEmailInfo("AsTyped@Example.test", true);

        UnlinkSteamResult result = await context.UseCase.ExecuteAsync(
            new UnlinkSteamCommand(
                context.User.Id,
                UnlinkOrigin.AuthMethod,
                true),
            default);

        Assert.True(result.Succeeded);
        EmailMessage message = Assert.Single(context.Email.Messages);
        Assert.Equal("AsTyped@Example.test", message.ToAddress);
        Assert.Equal(EmailTemplate.AuthMethodRemoved, message.Template);
    }

    private static TestContext Create()
    {
        var clock = new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0));
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("ABCDEFGHJK"),
            AgeGateSource.SelfAttested,
            clock);
        PlatformLink link = user.LinkPlatform(Platform.Steam, GamingHandle.Create("Steam Player"));
        var users = new FakeUserRepository();
        users.Users[user.Id] = user;
        var accounts = new FakeAccountGateway();
        var sessions = new FakeAccountSessionService();
        var unitOfWork = new FakeUnitOfWork();
        var email = new FakeEmailDispatcher();
        var useCase = new UnlinkSteamUseCase(
            users,
            accounts,
            sessions,
            unitOfWork,
            email);
        return new TestContext(useCase, user, link, accounts, sessions, unitOfWork, email);
    }

    private sealed record TestContext(
        UnlinkSteamUseCase UseCase,
        User User,
        PlatformLink Link,
        FakeAccountGateway Accounts,
        FakeAccountSessionService Sessions,
        FakeUnitOfWork UnitOfWork,
        FakeEmailDispatcher Email);
}
