using System.Reflection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class CompleteExternalSignInUseCaseTests
{
    [Theory]
    [InlineData(ExternalProviderPolicy.Google)]
    [InlineData(ExternalProviderPolicy.Steam)]
    public async Task Execute_LinksAProviderToTheBoundAuthenticatedUser_D087(string provider)
    {
        TestContext context = Create();
        UserId owner = UserId.New();
        context.Users.Add(
            User.BeginOnboarding(
                owner,
                FriendCode.Create("CDEFGHJKMN"),
                AgeGateSource.SelfAttested,
                new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0))));

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            new CompleteExternalSignInCommand(
                provider,
                "provider-key",
                null,
                false,
                ExternalStartMode.Link,
                false,
                owner,
                owner),
            default);

        Assert.True(result.Succeeded);
        Assert.True(result.MethodLinked);
        Assert.False(result.ShouldSignIn);
        Assert.Contains(
            context.Accounts.Logins,
            login =>
                login.Id == owner &&
                login.Provider == provider &&
                login.ProviderKey == "provider-key");
        Assert.Empty(context.Accounts.Created);
    }

    [Theory]
    [InlineData(ExternalProviderPolicy.Google)]
    [InlineData(ExternalProviderPolicy.Steam)]
    public async Task Execute_SignsInWithAnAlreadyLinkedProvider_D067(string provider)
    {
        TestContext context = Create();
        UserId owner = UserId.New();
        context.Accounts.LoginUsers[(provider, "provider-key")] = owner;
        context.Users.Add(
            User.BeginOnboarding(
                owner,
                FriendCode.Create("BCDEFGHJKM"),
                AgeGateSource.SelfAttested,
                new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0))));

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            LoginCommand(
                provider: provider,
                email: provider == ExternalProviderPolicy.Google
                    ? "Player@Example.test"
                    : null,
                emailVerified: provider == ExternalProviderPolicy.Google),
            default);

        Assert.True(result.Succeeded);
        Assert.True(result.ShouldSignIn);
        Assert.Equal(owner, result.UserId);
        Assert.Empty(context.Accounts.Created);
        Assert.Empty(context.Accounts.Logins);
    }

    [Fact]
    public async Task Execute_LinksGoogleToAMatchingVerifiedEmail_D067()
    {
        TestContext context = Create();
        UserId existing = SeedEmail(context, confirmed: true);

        ExternalSignInResult result = await ExecuteGoogleAsync(context, emailVerified: true);

        Assert.Equal(existing, result.UserId);
        Assert.Equal(existing, Assert.Single(context.Accounts.Logins).Id);
    }

    [Fact]
    public async Task Execute_RefusesToAutoLinkAnUnverifiedProviderEmail_D067()
    {
        TestContext context = Create();
        SeedEmail(context, confirmed: true);

        ExternalSignInResult result = await ExecuteGoogleAsync(context, emailVerified: false);

        Assert.Equal("link_required", result.Code);
        Assert.Empty(context.Accounts.Logins);
    }

    [Fact]
    public async Task Execute_RefusesToAutoLinkAnUnconfirmedLocalAccount_D067()
    {
        TestContext context = Create();
        SeedEmail(context, confirmed: false);

        ExternalSignInResult result = await ExecuteGoogleAsync(context, emailVerified: true);

        Assert.Equal("link_required", result.Code);
        Assert.Empty(context.Accounts.Logins);
    }

    [Fact]
    public async Task Execute_TreatsAnAbsentEmailVerifiedClaimAsUnverified_D067()
    {
        TestContext context = Create();

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            LoginCommand(email: "Player@Example.test", emailVerified: false),
            default);

        Assert.Equal("link_required", result.Code);
        Assert.Empty(context.Accounts.Created);
        Assert.Empty(context.Accounts.Logins);
    }

    [Fact]
    public async Task Execute_RejectsAnUnattestedAgeGateOnProviderAccountCreation_D083()
    {
        TestContext context = Create();

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            LoginCommand(ageGateAttested: false),
            default);

        Assert.Equal("age_gate_required", result.Code);
        Assert.Empty(context.Users.Added);
        Assert.Empty(context.Accounts.Created);
    }

    [Fact]
    public async Task Execute_RecordsTheAgeGateSourceWithoutABirthDate_D093()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(LoginCommand(), default);

        User created = Assert.Single(context.Users.Added);
        Assert.Equal(AgeGateSource.SelfAttested, created.AgeGateSource);
    }

    [Fact]
    public async Task Execute_NotifiesTheAccountAddressWhenAMethodIsLinked_D087()
    {
        TestContext context = Create();
        SeedEmail(context, confirmed: true);

        await ExecuteGoogleAsync(context, emailVerified: true);

        Assert.Equal(EmailTemplate.AuthMethodAdded, Assert.Single(context.Email.Messages).Template);
    }

    [Fact]
    public async Task Execute_WritesTheVerifiedGoogleAddressOnCreate_D067()
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            LoginCommand(email: "Player@Example.test", emailVerified: true),
            default);

        var created = Assert.Single(context.Accounts.Created);
        Assert.Equal("Player@Example.test", created.Email);
        Assert.True(created.Confirmed);
    }

    [Fact]
    public async Task Execute_RefusesToCreateAnAccountFromAnUnverifiedGoogleEmail_D067()
    {
        TestContext context = Create();

        ExternalSignInResult result = await ExecuteGoogleAsync(context, emailVerified: false);

        Assert.Equal("link_required", result.Code);
        Assert.Empty(context.Accounts.Created);
    }

    [Fact]
    public async Task Execute_RefusesToCreateAnAccountWhenTheAddressWasTakenMeanwhile_D067()
    {
        TestContext context = Create();
        context.Accounts.EmailLookupResults.Enqueue(null);
        context.Accounts.EmailLookupResults.Enqueue(UserId.New());

        ExternalSignInResult result = await ExecuteGoogleAsync(context, emailVerified: true);

        Assert.Equal("email_unavailable", result.Code);
        Assert.Empty(context.Accounts.Created);
    }

    [Fact]
    public async Task Execute_CreatesAnAddresslessAccountFromASteamAssertion_D078()
    {
        TestContext context = Create();

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            LoginCommand(provider: ExternalProviderPolicy.Steam, email: null, emailVerified: false),
            default);

        Assert.True(result.Succeeded);
        Assert.Null(Assert.Single(context.Accounts.Created).Email);
    }

    [Fact]
    public async Task Execute_CreatesAPlatformLinkAlongsideTheSteamAuthMethod_D032()
    {
        TestContext context = Create();

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            LoginCommand(provider: ExternalProviderPolicy.Steam, email: null, emailVerified: false),
            default);

        Assert.True(result.Succeeded);
        User user = Assert.Single(context.Users.Added);
        PlatformLink link = Assert.Single(user.PlatformLinks);
        Assert.Equal(Platform.Steam, link.Platform);
        Assert.Equal("Steam Player", link.Handle.Value);
        var identity = Assert.Single(context.Accounts.SteamIdentities);
        Assert.Equal("provider-key", identity.SteamId64);
        Assert.Equal("Steam Player", identity.PersonaName);
        Assert.Contains(
            context.Accounts.Logins,
            login => login.Id == user.Id && login.Provider == ExternalProviderPolicy.Steam);
    }

    [Fact]
    public async Task Execute_UsesTheSteamIdSuffixWhenTheProfileIsUnavailable()
    {
        TestContext context = Create(steamProfileAvailable: false);

        ExternalSignInResult result = await context.UseCase.ExecuteAsync(
            LoginCommand(
                provider: ExternalProviderPolicy.Steam,
                providerKey: "76561198000001234",
                email: null,
                emailVerified: false),
            default);

        Assert.True(result.Succeeded);
        User user = Assert.Single(context.Users.Added);
        Assert.Equal("Steam 1234", Assert.Single(user.PlatformLinks).Handle.Value);
        var identity = Assert.Single(context.Accounts.SteamIdentities);
        Assert.Equal("Steam 1234", identity.PersonaName);
        Assert.Null(identity.AvatarUrl);
        Assert.Contains(
            context.Accounts.Logins,
            login => login.Id == user.Id && login.Provider == ExternalProviderPolicy.Steam);
    }

    [Theory]
    [MemberData(nameof(SupportedProviderDeletionCases))]
    public async Task Execute_NeverCreatesAnAddresslessAccountForAProviderWithNoPublicDeletionLeg_D072(
        string provider,
        bool hasPublicDeletionLeg)
    {
        TestContext context = Create();

        await context.UseCase.ExecuteAsync(
            LoginCommand(provider: provider, email: null, emailVerified: false),
            default);

        bool createdWithoutAddress = context.Accounts.Created.Any(created => created.Email is null);
        Assert.Equal(hasPublicDeletionLeg, createdWithoutAddress);
    }

    public static IEnumerable<object[]> SupportedProviderDeletionCases()
    {
        IEnumerable<string> providers = typeof(ExternalProviderPolicy)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field =>
                field.IsLiteral &&
                !field.IsInitOnly &&
                field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        foreach (string provider in providers)
        {
            bool hasPublicDeletionLeg = provider switch
            {
                ExternalProviderPolicy.Google => false,
                ExternalProviderPolicy.Steam => true,
                _ => throw new InvalidOperationException(
                    $"Classify provider '{provider}' before enabling account creation."),
            };

            yield return [provider, hasPublicDeletionLeg];
        }
    }

    private static async Task<ExternalSignInResult> ExecuteGoogleAsync(
        TestContext context,
        bool emailVerified) =>
        await context.UseCase.ExecuteAsync(
            LoginCommand(emailVerified: emailVerified),
            default);

    private static UserId SeedEmail(TestContext context, bool confirmed)
    {
        UserId id = UserId.New();
        context.Accounts.EmailUsers["PLAYER@EXAMPLE.TEST"] = id;
        context.Accounts.Emails[id] = new AccountEmailInfo("Player@Example.test", confirmed);
        return id;
    }

    private static CompleteExternalSignInCommand LoginCommand(
        string provider = ExternalProviderPolicy.Google,
        string providerKey = "provider-key",
        string? email = "Player@Example.test",
        bool emailVerified = true,
        bool ageGateAttested = true) =>
        new(
            provider,
            providerKey,
            email,
            emailVerified,
            ExternalStartMode.Login,
            ageGateAttested,
            null,
            null);

    private static TestContext Create(bool steamProfileAvailable = true)
    {
        var accounts = new FakeAccountGateway();
        var users = new FakeUserRepository();
        var email = new FakeEmailDispatcher();
        var useCase = new CompleteExternalSignInUseCase(
            accounts,
            users,
            new FakeFriendCodeGenerator(),
            new FakeAccountSessionService(),
            new FakeTokenService(),
            new FakeUnitOfWork(),
            email,
            new FakeSteamProfileClient
            {
                Profile = steamProfileAvailable
                    ? new SteamProfile("Steam Player", "https://cdn.example/avatar.jpg")
                    : null,
            },
            new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0)));
        return new TestContext(useCase, accounts, users, email);
    }

    private sealed record TestContext(
        CompleteExternalSignInUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeUserRepository Users,
        FakeEmailDispatcher Email);
}
