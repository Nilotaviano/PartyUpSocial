using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Domain.Kernel;

namespace PartyUp.Application.Tests.Accounts;

public sealed class SignInWithPasswordUseCaseTests
{
    [Fact]
    public async Task Execute_ReturnsTheSameFailureForUnknownWrongPasswordlessAndLockedOutAccounts_D067()
    {
        PasswordSignInResult unknown = await RunNegativeAsync(null, null);
        PasswordSignInResult wrong = await RunNegativeAsync(
            UserId.New(),
            new PasswordCheckResult(false, false, true));
        PasswordSignInResult passwordless = await RunNegativeAsync(
            UserId.New(),
            new PasswordCheckResult(false, false, false));
        PasswordSignInResult locked = await RunNegativeAsync(
            UserId.New(),
            new PasswordCheckResult(false, true, false));

        Assert.Equal(unknown, wrong);
        Assert.Equal(unknown, passwordless);
        Assert.Equal(unknown, locked);

        var successGateway = new FakeAccountGateway();
        UserId successId = UserId.New();
        successGateway.EmailUsers["player@example.test"] = successId;
        successGateway.PasswordChecks[successId] = new PasswordCheckResult(true, false, true);
        PasswordSignInResult success = await new SignInWithPasswordUseCase(successGateway)
            .ExecuteAsync("player@example.test", "correct", default);
        Assert.True(success.Succeeded);
        Assert.Equal(1, successGateway.RealVerifies + successGateway.DummyVerifies);
    }

    private static async Task<PasswordSignInResult> RunNegativeAsync(
        UserId? userId,
        PasswordCheckResult? check)
    {
        var gateway = new FakeAccountGateway();
        if (userId is { } id)
        {
            gateway.EmailUsers["player@example.test"] = id;
            gateway.PasswordChecks[id] = check!;
        }

        PasswordSignInResult result = await new SignInWithPasswordUseCase(gateway)
            .ExecuteAsync("player@example.test", "wrong", default);
        Assert.Equal("invalid_credentials", result.Code);
        Assert.Equal(1, gateway.RealVerifies + gateway.DummyVerifies);
        return result;
    }

    [Fact]
    public async Task Execute_SignsInOnlyThroughTheGate_D180()
    {
        var gateway = new FakeAccountGateway();
        UserId id = UserId.New();
        gateway.EmailUsers["player@example.test"] = id;
        gateway.PasswordChecks[id] = new PasswordCheckResult(true, false, true);

        PasswordSignInResult result = await new SignInWithPasswordUseCase(gateway)
            .ExecuteAsync("player@example.test", "correct", default);

        Assert.True(result.Succeeded);
        Assert.Equal(id, result.UserId);
    }
}
