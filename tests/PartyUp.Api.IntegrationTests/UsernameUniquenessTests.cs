using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category","Integration")]
public sealed class UsernameUniquenessTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Claim_RejectsACaseVariantAtTheDatabaseLevel_D128()
    {
        await using (AppDbContext firstContext = CreateContext())
        {
            await firstContext.Database.MigrateAsync();
            User firstOwner = User.BeginOnboarding(
                UserId.New(),
                FriendCode.Create("PU-ABC123"),
                AgeGateSource.SelfAttested,
                SystemClock.Instance);
            UsernameRegistryEntry firstClaim = UsernameRegistryEntry.ClaimNew(
                Username.Create("CaseVariant"),
                firstOwner.Id);

            firstContext.Users.Add(firstOwner);
            firstContext.UsernameRegistry.Add(firstClaim);
            await firstContext.SaveChangesAsync();
        }

        await using AppDbContext secondContext = CreateContext();
        User secondOwner = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("PU-XYZ789"),
            AgeGateSource.SelfAttested,
            SystemClock.Instance);
        UsernameRegistryEntry caseVariant = UsernameRegistryEntry.ClaimNew(
            Username.Create("casevariant"),
            secondOwner.Id);

        secondContext.Users.Add(secondOwner);
        secondContext.UsernameRegistry.Add(caseVariant);

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
