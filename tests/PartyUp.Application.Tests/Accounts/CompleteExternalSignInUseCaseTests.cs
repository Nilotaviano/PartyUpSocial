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
        string? email = "Player@Example.test",
        bool emailVerified = true,
        bool ageGateAttested = true) =>
        new(
            provider,
            "provider-key",
            email,
            emailVerified,
            ExternalStartMode.Login,
            ageGateAttested,
            null,
            null);

    private static TestContext Create()
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
            new TestClock(Instant.FromUtc(2026, 7, 31, 12, 0)));
        return new TestContext(useCase, accounts, users, email);
    }

    private sealed record TestContext(
        CompleteExternalSignInUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeUserRepository Users,
        FakeEmailDispatcher Email);
}
