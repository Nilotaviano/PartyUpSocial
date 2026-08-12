using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Friends;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Profile;

/// <summary>
/// AVATARS_DESIGN.md §3.4's deletion tail: <c>UserDataScope.AvatarSources</c> purges every captured
/// picture the user's linked accounts ever supplied, inside the same grace-expiry transaction
/// (DOMAIN_MODEL.md §11 step 5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvatarSourceDeletionPurgeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ExpiryPurgesEveryAvatarSourceRow_D190()
    {
        Phase3DeletionSeed seed = await Phase3DeletionTestSupport.SeedAsync(_factory);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User user = await context.DomainUsers.SingleAsync(u => u.Id == seed.SubjectId);
            user.CaptureAvatarSource(
                AvatarSourceKind.SteamPlatform,
                [1, 2, 3],
                "image/webp",
                _factory.Clock);
            await context.SaveChangesAsync();
        }

        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Assert.True(await Phase3DeletionTestSupport.CreateUseCase(scope.ServiceProvider)
                .ExecuteAsync(seed.SubjectId, default));
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await verify.AvatarSources.CountAsync(source => source.UserId == seed.SubjectId));
    }
}
