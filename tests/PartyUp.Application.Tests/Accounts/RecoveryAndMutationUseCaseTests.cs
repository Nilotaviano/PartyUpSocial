using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Accounts;

public sealed class RequestPasswordResetUseCaseTests
{
    [Fact]
    public async Task Execute_SendsRecoveryMailToAKnownAddress_D067()
    {
        var accounts = new FakeAccountGateway();
        UserId id = UserId.New();
        accounts.EmailUsers["player@example.test"] = id;
        var users = new FakeUserRepository();
        var email = new FakeEmailDispatcher();
        var tokens = new FakeTokenService();
        var useCase = new RequestPasswordResetUseCase(
            accounts,
            users,
            tokens,
            new FakeUnitOfWork(),
            email,
            new AccountEmailOptions("https://partyup.social"));

        await useCase.ExecuteAsync("player@example.test", default);

        Assert.Equal(EmailTemplate.PasswordReset, Assert.Single(email.Messages).Template);
        Assert.Equal(id, Assert.Single(tokens.Issues).UserId);
    }
}

public sealed class ResetPasswordUseCaseTests
{
    [Fact]
    public async Task Execute_SignsInOnlyThroughTheGate_D165()
    {
        UserId id = UserId.New();
        var tokens = new FakeTokenService();
        tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.PasswordReset,
            id,
            null,
            null,
            null,
            Instant.FromUtc(2026, 7, 31, 12, 0)));
        var accounts = new FakeAccountGateway();
        var sessions = new FakeAccountSessionService();
        var useCase = new ResetPasswordUseCase(
            accounts,
            sessions,
            tokens,
            new FakeUnitOfWork());

        PasswordResetResult result = await useCase.ExecuteAsync(
            "token",
            "new-password-value",
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(id, result.UserId);
        Assert.Equal(1, accounts.PasswordSets);
        Assert.Equal(1, sessions.Rotations);
    }
}

public sealed class SetPasswordUseCaseTests
{
    [Fact]
    public async Task Execute_RequiresAVerifiedEmail_D067()
    {
        TestContext context = Create(confirmedEmail: false, validProof: true);

        AccountMutationResult result = await context.UseCase.ExecuteAsync(
            context.UserId,
            "new-password-value",
            "current",
            null,
            default);

        Assert.Equal("email_required", result.Code);
        Assert.Equal(0, context.Accounts.PasswordSets);
    }

    [Fact]
    public async Task Execute_RequiresReauthentication_D067()
    {
        TestContext context = Create(confirmedEmail: true, validProof: false);

        AccountMutationResult result = await context.UseCase.ExecuteAsync(
            context.UserId,
            "new-password-value",
            null,
            null,
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.Equal(0, context.Accounts.PasswordSets);
    }

    [Fact]
    public async Task Execute_RotatesTheSecurityStamp_D165()
    {
        TestContext context = Create(confirmedEmail: true, validProof: true);

        await context.UseCase.ExecuteAsync(
            context.UserId,
            "new-password-value",
            "current",
            null,
            default);

        Assert.Equal(1, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_NotifiesTheAccountAddress_D087()
    {
        TestContext context = Create(confirmedEmail: true, validProof: true);

        await context.UseCase.ExecuteAsync(
            context.UserId,
            "new-password-value",
            "current",
            null,
            default);

        Assert.Equal(EmailTemplate.AuthMethodAdded, Assert.Single(context.Email.Messages).Template);
    }

    private static TestContext Create(bool confirmedEmail, bool validProof)
    {
        UserId id = UserId.New();
        var accounts = new FakeAccountGateway();
        accounts.Emails[id] = confirmedEmail
            ? new AccountEmailInfo("player@example.test", true)
            : null;
        accounts.PasswordChecks[id] = validProof
            ? new PasswordCheckResult(true, false, true)
            : new PasswordCheckResult(false, false, true);
        var users = new FakeUserRepository();
        var sessions = new FakeAccountSessionService();
        var tokens = new FakeTokenService();
        var email = new FakeEmailDispatcher();
        var reauthenticate = new ReauthenticateUseCase(
            accounts,
            users,
            tokens,
            new FakeUnitOfWork(),
            email,
            new AccountEmailOptions("https://partyup.social"));
        var useCase = new SetPasswordUseCase(
            accounts,
            users,
            sessions,
            reauthenticate,
            new FakeUnitOfWork(),
            email);
        return new TestContext(id, useCase, accounts, sessions, email);
    }

    private sealed record TestContext(
        UserId UserId,
        SetPasswordUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeAccountSessionService Sessions,
        FakeEmailDispatcher Email);
}

public sealed class ChangeEmailUseCaseTests
{
    [Fact]
    public async Task Execute_RequiresTheCurrentPassword_D067()
    {
        TestContext context = Create(hasPreviousAddress: true, validPassword: false);

        AccountMutationResult result = await context.UseCase.RequestAsync(
            context.UserId,
            "new@example.test",
            "wrong",
            null,
            default);

        Assert.Equal("reauth_required", result.Code);
        Assert.Empty(context.Tokens.Issues);
    }

    [Fact]
    public async Task Execute_NotifiesThePreviousAddress_D067()
    {
        TestContext context = Create(hasPreviousAddress: true, validPassword: true);

        await context.UseCase.RequestAsync(
            context.UserId,
            "new@example.test",
            "correct",
            null,
            default);

        Assert.Contains(
            context.Email.Messages,
            message => message.Template == EmailTemplate.EmailChangeRequested &&
                message.ToAddress == "old@example.test");
    }

    [Fact]
    public async Task Execute_ConfirmsTheTokensTypedAddressNotTheRowsAddress_D067()
    {
        TestContext context = Create(hasPreviousAddress: true, validPassword: true);
        context.Tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.EmailChange,
            context.UserId,
            "TOKEN@EXAMPLE.TEST",
            null,
            null,
            Instant.FromUtc(2026, 7, 31, 12, 0),
            "Token@Example.Test"));

        AccountMutationResult result = await context.UseCase.VerifyAsync("token", default);

        Assert.True(result.Succeeded);
        Assert.Equal("Token@Example.Test", context.Accounts.Emails[context.UserId]!.Email);
    }

    [Fact]
    public async Task Execute_PreservesTheMailboxAddressCaseThroughRequestAndVerification()
    {
        TestContext context = Create(hasPreviousAddress: true, validPassword: true);

        await context.UseCase.RequestAsync(
            context.UserId,
            "NewAddress@corp.example",
            "correct",
            null,
            default);

        IssueTokenRequest issue = Assert.Single(context.Tokens.Issues);
        Assert.Equal("NEWADDRESS@CORP.EXAMPLE", issue.EmailNormalized);
        Assert.Equal("NewAddress@corp.example", issue.Email);
        Assert.Contains(
            context.Email.Messages,
            message => message.Template == EmailTemplate.EmailChangeVerification &&
                message.ToAddress == "NewAddress@corp.example");

        context.Tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.EmailChange,
            context.UserId,
            issue.EmailNormalized,
            null,
            null,
            Instant.FromUtc(2026, 7, 31, 12, 0),
            issue.Email));

        await context.UseCase.VerifyAsync("token", default);

        Assert.Equal(
            "NewAddress@corp.example",
            context.Accounts.Emails[context.UserId]!.Email);
    }

    [Fact]
    public async Task Execute_RotatesTheSecurityStampOnVerify_D165()
    {
        TestContext context = Create(hasPreviousAddress: true, validPassword: true);
        context.Tokens.RedemptionResults.Enqueue(new TokenRedemption(
            true,
            null,
            AuthTokenPurpose.EmailChange,
            context.UserId,
            "NEW@EXAMPLE.TEST",
            null,
            null,
            Instant.FromUtc(2026, 7, 31, 12, 0)));

        await context.UseCase.VerifyAsync("token", default);

        Assert.Equal(1, context.Sessions.Rotations);
    }

    [Fact]
    public async Task Execute_AddsAFirstAddressToAnAddresslessAccount_D067()
    {
        TestContext context = Create(hasPreviousAddress: false, validPassword: false);
        context.Tokens.RedemptionResults.Enqueue(ReauthProof(context.UserId));

        AccountMutationResult result = await context.UseCase.RequestAsync(
            context.UserId,
            "first@example.test",
            null,
            "reauth-token",
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(AuthTokenPurpose.EmailChange, context.Tokens.Issues[0].Purpose);
    }

    [Fact]
    public async Task Execute_SendsNoPreviousAddressNoticeWhenThereIsNone_D078()
    {
        TestContext context = Create(hasPreviousAddress: false, validPassword: false);
        context.Tokens.RedemptionResults.Enqueue(ReauthProof(context.UserId));

        await context.UseCase.RequestAsync(
            context.UserId,
            "first@example.test",
            null,
            "reauth-token",
            default);

        Assert.DoesNotContain(
            context.Email.Messages,
            message => message.Template == EmailTemplate.EmailChangeRequested);
    }

    private static TestContext Create(bool hasPreviousAddress, bool validPassword)
    {
        UserId id = UserId.New();
        var accounts = new FakeAccountGateway();
        accounts.Emails[id] = hasPreviousAddress
            ? new AccountEmailInfo("old@example.test", true)
            : null;
        accounts.PasswordChecks[id] = validPassword
            ? new PasswordCheckResult(true, false, true)
            : new PasswordCheckResult(false, false, true);
        var users = new FakeUserRepository();
        var sessions = new FakeAccountSessionService();
        var tokens = new FakeTokenService();
        var email = new FakeEmailDispatcher();
        var reauthenticate = new ReauthenticateUseCase(
            accounts,
            users,
            tokens,
            new FakeUnitOfWork(),
            email,
            new AccountEmailOptions("https://partyup.social"));
        var useCase = new ChangeEmailUseCase(
            accounts,
            users,
            sessions,
            reauthenticate,
            tokens,
            new FakeUnitOfWork(),
            email,
            new AccountEmailOptions("https://partyup.social"));
        return new TestContext(id, useCase, accounts, sessions, tokens, email);
    }

    private static TokenRedemption ReauthProof(UserId id) =>
        new(
            true,
            null,
            AuthTokenPurpose.Reauthentication,
            id,
            null,
            null,
            null,
            Instant.FromUtc(2026, 7, 31, 12, 0));

    private sealed record TestContext(
        UserId UserId,
        ChangeEmailUseCase UseCase,
        FakeAccountGateway Accounts,
        FakeAccountSessionService Sessions,
        FakeTokenService Tokens,
        FakeEmailDispatcher Email);
}
