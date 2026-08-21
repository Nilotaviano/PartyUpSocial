using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// D-225's poll arm over HTTP: a claimed poll guest links the seat to their account and their votes
/// come with it; the spent link is terminal, and a caller without the guest cookie is answered
/// exactly like a caller for a poll that does not exist.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollGuestUpgradeTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Upgrade_MovesTheVoterAndItsVotesOntoTheAccount_D225()
    {
        (Guid pollId, Guid optionId, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string guestCookie = await ExchangeAsync(guestClient, token);
        await ClaimAsync(guestClient, guestLinkId, guestCookie, "Robin");

        HttpResponseMessage voted = await AntiforgeryTestClient.SendAsync(
            guestClient,
            HttpMethod.Put,
            $"/api/v1/guest/{guestLinkId:N}/poll/options/{optionId:D}/vote",
            JsonContent.Create(new { choice = "available" }),
            guestCookie);
        Assert.Equal(HttpStatusCode.OK, voted.StatusCode);

        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);

        HttpResponseMessage upgrade = await AntiforgeryTestClient.PostAsync(
            session.Client,
            $"/api/v1/guest/{guestLinkId:N}/poll/upgrade",
            content: null,
            $"{guestCookie}; {session.AuthCookie}");

        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(upgrade);
        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);
        Assert.Equal(pollId, body.RootElement.GetProperty("pollId").GetGuid());

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PollId typedPollId = PollId.From(pollId);

        PollParticipant voter = await context.PollParticipants
            .SingleAsync(p => p.PollId == typedPollId && p.UserId == account.Id);
        Assert.Equal(PollParticipantStatus.Active, voter.Status);
        Assert.Empty(context.PollParticipants.Where(p => p.PollId == typedPollId && p.GuestIdentityId != null));

        PollVote vote = await context.PollVotes
            .SingleAsync(v => v.PollId == typedPollId && v.UserId == account.Id);
        Assert.Equal(VoteChoice.Available, vote.Choice);
        Assert.Empty(context.PollVotes.Where(v => v.PollId == typedPollId && v.GuestIdentityId != null));

        GuestLink link = await context.GuestLinks.SingleAsync(l => l.Id == GuestLinkId.From(guestLinkId));
        Assert.Equal(GuestLinkState.Upgraded, link.State);
        GuestIdentity identity = await context.GuestIdentities.SingleAsync(g => g.PollId == typedPollId);
        Assert.Equal(account.Id, identity.UpgradedToUserId);

        // The spent link now serves the terminal notice instead of the live poll.
        var reread = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/guest/{guestLinkId:N}/poll");
        reread.Headers.Add(HeaderNames.Cookie, guestCookie);
        using JsonDocument view = await SessionEndpointTestSupport.SuccessJsonAsync(
            await guestClient.SendAsync(reread));
        Assert.Equal("revoked", view.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Upgrade_WithoutTheGuestCookie_IsThePollNotFound404_D225()
    {
        (_, _, Guid guestLinkId, string token) = await SeedPollWithGuestLinkAsync();
        using HttpClient guestClient = CreateGuestClient();
        string guestCookie = await ExchangeAsync(guestClient, token);
        await ClaimAsync(guestClient, guestLinkId, guestCookie, "Robin");

        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);

        HttpResponseMessage upgrade = await session.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/guest/{guestLinkId:N}/poll/upgrade");

        await SessionEndpointTestSupport.AssertProblemAsync(
            upgrade, HttpStatusCode.NotFound, "poll_not_found");
    }

    // ---- fixture helpers -------------------------------------------------------------------------

    private static async Task<string> ExchangeAsync(HttpClient client, string token)
    {
        HttpResponseMessage response = await client.GetAsync($"/g/{token}");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        SetCookieHeaderValue cookie = GuestExchangeTests.SetCookiesOf(response)
            .Single(c => c.Name.Value!.StartsWith("pu.g.", StringComparison.Ordinal));
        return GuestExchangeTests.CookieHeader(cookie);
    }

    private static async Task ClaimAsync(
        HttpClient client, Guid guestLinkId, string cookieHeader, string displayName)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.SendAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/guest/{guestLinkId:N}/poll/claim",
            JsonContent.Create(new { displayName }),
            cookieHeader);
        using JsonDocument _ = await SessionEndpointTestSupport.SuccessJsonAsync(response);
    }

    private HttpClient CreateGuestClient() =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });

    private async Task<(Guid PollId, Guid OptionId, Guid GuestLinkId, string Token)> SeedPollWithGuestLinkAsync()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-upgrade-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Guest upgrade poll",
                options = new[]
                {
                    new { startTime = SessionEndpointTestSupport.Iso(baseTime) },
                    new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)) },
                },
                visibility = "private",
                minAttendance = 1,
                guestLinkCount = 1,
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = body.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();
        Guid guestLinkId = body.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();
        string url = body.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string token = url[(url.LastIndexOf("/g/", StringComparison.Ordinal) + 3)..];

        return (pollId, optionId, guestLinkId, token);
    }
}
