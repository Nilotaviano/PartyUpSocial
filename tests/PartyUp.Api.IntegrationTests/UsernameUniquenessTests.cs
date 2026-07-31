using Microsoft.EntityFrameworkCore;
using NodaTime;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;
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

            firstContext.DomainUsers.Add(firstOwner);
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

        secondContext.DomainUsers.Add(secondOwner);
        secondContext.UsernameRegistry.Add(caseVariant);

        await Assert.ThrowsAsync<DbUpdateException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Find_HoldsTheUsernameRegistryRowLockUntilTheTransactionCompletes()
    {
        const string canonical = "locked.player";
        await using (AppDbContext setup = CreateContext())
        {
            await setup.Database.MigrateAsync();
            User owner = User.BeginOnboarding(
                UserId.New(),
                FriendCode.Create("PU-LOCK01"),
                AgeGateSource.SelfAttested,
                SystemClock.Instance);
            setup.DomainUsers.Add(owner);
            setup.UsernameRegistry.Add(
                UsernameRegistryEntry.ClaimNew(
                    Username.Create("Locked.Player"),
                    owner.Id));
            await setup.SaveChangesAsync();
        }

        await using AppDbContext firstContext = CreateContext();
        await using var firstTransaction = await firstContext.Database.BeginTransactionAsync();
        var firstRepository = new UsernameRegistryRepository(firstContext);
        UsernameRegistryEntry? locked = await firstRepository.FindAsync(canonical, default);
        Assert.NotNull(locked);

        await using AppDbContext competingContext = CreateContext();
        await competingContext.Database.OpenConnectionAsync();
        var competingRepository = new UsernameRegistryRepository(competingContext);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => competingRepository.FindAsync(canonical, timeout.Token));

        await firstTransaction.CommitAsync();
        await using AppDbContext verificationContext = CreateContext();
        var verificationRepository = new UsernameRegistryRepository(verificationContext);
        Assert.NotNull(await verificationRepository.FindAsync(canonical, default));
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
