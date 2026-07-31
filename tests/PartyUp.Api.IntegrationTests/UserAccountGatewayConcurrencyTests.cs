using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class UserAccountGatewayConcurrencyTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulPasswordCheck_RetriesResetConcurrencyConflicts(
        bool throwException)
    {
        UserId userId = await _factory.SeedUserAsync(
            email: $"reset-conflict-{throwException}@example.test",
            password: Password);
        await SetAccessFailedCountAsync(userId);

        var probe = new ResetConflictProbe(userId.Value, throwException);
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserStore<AuthUser>>();
                services.AddScoped<IUserStore<AuthUser>>(provider =>
                    new ConflictingUserStore(
                        provider.GetRequiredService<AppDbContext>(),
                        probe));
            }));

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var gateway = scope.ServiceProvider.GetRequiredService<IUserAccountGateway>();
            PasswordCheckResult result = await gateway.CheckPasswordAsync(
                userId,
                Password,
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.False(result.IsLockedOut);
            Assert.True(result.HashVerified);
        }

        Assert.Equal(2, probe.UpdateAttempts);
        await using AsyncServiceScope verificationScope = factory.Services.CreateAsyncScope();
        var users = verificationScope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser account = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
        Assert.Equal(0, account.AccessFailedCount);
    }

    private async Task SetAccessFailedCountAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        AuthUser account = (await users.FindByIdAsync(userId.Value.ToString("D")))!;
        IdentityResult result = await users.AccessFailedAsync(account);
        Assert.True(result.Succeeded);
        Assert.Equal(1, account.AccessFailedCount);
    }

    private sealed class ConflictingUserStore(
        AppDbContext context,
        ResetConflictProbe probe)
        : UserStore<AuthUser, AuthRole, AppDbContext, Guid>(context)
    {
        public override Task<IdentityResult> UpdateAsync(
            AuthUser user,
            CancellationToken cancellationToken = default)
        {
            if (user.Id == probe.UserId && user.AccessFailedCount == 0)
            {
                int attempt = probe.RecordUpdateAttempt();
                if (attempt == 1 && probe.ThrowException)
                {
                    throw new DbUpdateConcurrencyException("Scripted reset conflict.");
                }

                if (attempt == 1)
                {
                    return Task.FromResult(IdentityResult.Failed(new IdentityError
                    {
                        Code = "ConcurrencyFailure",
                        Description = "Scripted reset conflict.",
                    }));
                }
            }

            return base.UpdateAsync(user, cancellationToken);
        }
    }

    private sealed class ResetConflictProbe(Guid userId, bool throwException)
    {
        private int _updateAttempts;

        internal Guid UserId { get; } = userId;

        internal bool ThrowException { get; } = throwException;

        internal int UpdateAttempts => Volatile.Read(ref _updateAttempts);

        internal int RecordUpdateAttempt() => Interlocked.Increment(ref _updateAttempts);
    }
}
