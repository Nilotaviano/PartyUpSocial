using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Identity;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class UnitOfWorkTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Rollback_UndoesAnIdentityAutoSave_D165()
    {
        UserId userId = await _factory.SeedUserAsync();
        string originalStamp;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            AuthUser authUser = (await userManager.FindByIdAsync(userId.Value.ToString("D")))!;
            originalStamp = authUser.SecurityStamp!;
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var sessions = scope.ServiceProvider.GetRequiredService<IAccountSessionService>();
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unitOfWork.ExecuteAsync<bool>(async cancellationToken =>
                {
                    User user = (await users.FindForUpdateAsync(userId, cancellationToken))!;
                    user.Ban(_factory.Clock);
                    await sessions.SignOutEverywhereAsync(userId, cancellationToken);
                    throw new InvalidOperationException("force rollback");
                }, default));
        }

        await using AsyncServiceScope verificationScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        AuthUser persistedAuthUser = await context.AuthUsers.AsNoTracking().SingleAsync(
            user => user.Id == userId.Value);
        User persistedUser = await context.DomainUsers.AsNoTracking().SingleAsync(
            user => user.Id == userId);
        Assert.Equal(originalStamp, persistedAuthUser.SecurityStamp);
        Assert.Null(persistedUser.BannedAt);
    }
}
