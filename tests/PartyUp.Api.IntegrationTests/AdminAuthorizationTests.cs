using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AdminAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Admin_ForANonAdmin_Returns404_D177()
    {
        UserId userId = await _factory.SeedUserAsync();
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/users");
        request.Headers.Add(HeaderNames.Cookie, cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_AfterAnOutOfBandRoleRevocation_Returns404_D177()
    {
        UserId userId = await _factory.SeedUserAsync(admin: true);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IdentityUserRole<Guid> role = await context.Set<IdentityUserRole<Guid>>()
                .SingleAsync(userRole => userRole.UserId == userId.Value);
            context.Remove(role);
            await context.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/users");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_CanListBanAndUnbanAUser_D179_D180()
    {
        UserId adminId = await _factory.SeedUserAsync(
            admin: true,
            email: "admin-list@example.test");
        UserId targetId = await _factory.SeedUserAsync(
            email: "target-list@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, adminId);

        using var listRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/admin/users?query=target-list&page=1&pageSize=10");
        listRequest.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        AdminUsersResponse list =
            (await listResponse.Content.ReadFromJsonAsync<AdminUsersResponse>())!;
        AdminUserResponse listed = Assert.Single(list.Users);
        Assert.Equal(targetId.Value, listed.Id);
        Assert.Equal("t***@example.test", listed.Email);
        Assert.Null(listed.BannedAt);

        string originalStamp = await SecurityStampAsync(targetId);
        HttpResponseMessage ban = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/users/{targetId.Value:D}/ban",
            JsonContent.Create(new { removeContent = false }),
            cookie);
        Assert.Equal(HttpStatusCode.OK, ban.StatusCode);
        Assert.NotEqual(originalStamp, await SecurityStampAsync(targetId));
        Assert.NotNull(await BannedAtAsync(targetId));

        HttpResponseMessage unban = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/users/{targetId.Value:D}/unban",
            content: null,
            cookie);
        Assert.Equal(HttpStatusCode.OK, unban.StatusCode);
        Assert.Null(await BannedAtAsync(targetId));

        HttpResponseMessage contentRemoval = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/users/{targetId.Value:D}/ban",
            JsonContent.Create(new { removeContent = true }),
            cookie);
        Assert.Equal(HttpStatusCode.NotImplemented, contentRemoval.StatusCode);
        Assert.Contains(
            "not_implemented_in_phase",
            await contentRemoval.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Null(await BannedAtAsync(targetId));
    }

    [Fact]
    public async Task Admin_BanRequestRejectsReasonNote_D180()
    {
        UserId adminId = await _factory.SeedUserAsync(admin: true);
        UserId targetId = await _factory.SeedUserAsync(
            email: "reason-note-target@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, adminId);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/users/{targetId.Value:D}/ban",
            JsonContent.Create(new { removeContent = false, reasonNote = "not accepted" }),
            cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await BannedAtAsync(targetId));
    }

    [Fact]
    public async Task Admin_CanReadTheDeletionJournalRestoreReplayFeed()
    {
        UserId adminId = await _factory.SeedUserAsync(admin: true);
        var entry = new DeletionJournalEntry(
            UserId.New(),
            NodaTime.Instant.FromUtc(2026, 7, 31, 11, 0));
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<IDeletionJournal>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ExecuteAsync(
                ct =>
                {
                    journal.Append(entry);
                    return Task.FromResult(true);
                },
                default);
        }
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, adminId);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/admin/deletion-journal?since=2026-07-31T10:00:00Z&limit=25");
        request.Headers.Add(HeaderNames.Cookie, cookie);

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        DeletionJournalResponse feed =
            (await response.Content.ReadFromJsonAsync<DeletionJournalResponse>())!;
        DeletionJournalItemResponse item = Assert.Single(feed.Entries);
        Assert.Equal(entry.UserId.Value, item.UserId);
        Assert.Equal("2026-07-31T11:00:00Z", item.ExecutedAt);
    }

    private async Task<string> SecurityStampAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await context.AuthUsers
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId.Value)).SecurityStamp!;
    }

    private async Task<NodaTime.Instant?> BannedAtAsync(UserId userId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await context.DomainUsers
            .AsNoTracking()
            .SingleAsync(user => user.Id == userId)).BannedAt;
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
}
