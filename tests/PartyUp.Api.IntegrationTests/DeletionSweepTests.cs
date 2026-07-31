using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Deletion;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DeletionSweepTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();
    public Task DisposeAsync() => _factory.StopAsync();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Sweep_AtGraceExpiry_TombstonesAndJournals_D148()
    {
        UserId due = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "sweep-due@example.test");
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "sweep-admin@example.test");
        _factory.Clock.Advance(User.DeletionGracePeriod);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);

        HttpResponseMessage response = await RunSweepAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadUserAsync(due)).IsTombstoned);
        IReadOnlyList<DeletionJournalEntry> journal = await ReadJournalAsync();
        Assert.Contains(journal, entry => entry.UserId == due);
    }

    [Fact]
    public async Task Sweep_WhereOneUserFails_CompletesTheOthersAndLeavesNoTombstoneWithoutItsJournalRow_D092()
    {
        UserId first = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "sweep-first@example.test");
        UserId failing = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "sweep-failing@example.test");
        UserId last = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "sweep-last@example.test");
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "sweep-failure-admin@example.test");
        _factory.Clock.Advance(User.DeletionGracePeriod);
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDeletionJournal>();
                services.AddScoped<IDeletionJournal>(provider =>
                    new FailingDeletionJournal(
                        new DeletionJournal(provider.GetRequiredService<
                            PartyUp.Infrastructure.Persistence.AppDbContext>()),
                        failing));
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);
        string cookie = await SignInAsync(client, admin);

        HttpResponseMessage response = await RunSweepAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DeletionMaintenanceResponse result =
            (await response.Content.ReadFromJsonAsync<DeletionMaintenanceResponse>())!;
        Assert.Equal(2, result.Executed);
        Assert.Equal(1, result.Failed);
        User[] users =
        [
            await ReadUserAsync(first),
            await ReadUserAsync(failing),
            await ReadUserAsync(last),
        ];
        Assert.True(users[0].IsTombstoned);
        Assert.True(users[1].IsPendingDeletion);
        Assert.True(users[2].IsTombstoned);
        IReadOnlySet<UserId> journalled = (await ReadJournalAsync())
            .Select(entry => entry.UserId)
            .ToHashSet();
        Assert.All(users.Where(user => user.IsTombstoned),
            user => Assert.Contains(user.Id, journalled));
        Assert.DoesNotContain(failing, journalled);
    }

    private static Task<HttpResponseMessage> RunSweepAsync(HttpClient client, string cookie) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/admin/maintenance/deletion-sweep",
            content: null,
            cookie);

    private async Task<User> ReadUserAsync(UserId id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .FindAsync(id, default))!;
    }

    private async Task<IReadOnlyList<DeletionJournalEntry>> ReadJournalAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDeletionJournal>()
            .ReadSinceAsync(Instant.MinValue, 100, default);
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private sealed class FailingDeletionJournal(
        IDeletionJournal inner,
        UserId failing) : IDeletionJournal
    {
        public void Append(DeletionJournalEntry entry)
        {
            if (entry.UserId == failing)
            {
                throw new InvalidOperationException("injected journal failure");
            }

            inner.Append(entry);
        }

        public Task<IReadOnlyList<DeletionJournalEntry>> ReadSinceAsync(
            Instant since,
            int limit,
            CancellationToken cancellationToken) =>
            inner.ReadSinceAsync(since, limit, cancellationToken);
    }
}
