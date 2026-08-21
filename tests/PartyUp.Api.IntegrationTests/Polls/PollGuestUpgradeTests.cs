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

    /// <summary>
    /// D-225's duplicate branch over HTTP: the account already votes in this poll, so its own
    /// participant and votes stand and the guest's leave. The guest rows are severed from the
    /// aggregate but their FKs do not cascade, so a merge that forgets to erase them explicitly
    /// fails the transaction — this is the regression that keeps that erase in place.
    /// </summary>
    [Fact]
    public async Task Upgrade_WhenTheAccountAlreadyVotes_KeepsItsVotesAndErasesTheGuestRows_D225()
    {
        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        SeededGuestPoll seeded = await SeedPollWithGuestLinkAsync(invitee: account.Id);

        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);
        HttpResponseMessage ownVote = await session.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/polls/{seeded.PollId:D}/options/{seeded.FirstOptionId:D}/vote",
            JsonContent.Create(new { choice = "unavailable" }));
        Assert.True(ownVote.IsSuccessStatusCode, await ownVote.Content.ReadAsStringAsync());

        using HttpClient guestClient = CreateGuestClient();
        string guestCookie = await ExchangeAsync(guestClient, seeded.Token);
        await ClaimAsync(guestClient, seeded.GuestLinkId, guestCookie, "Robin");
        foreach (Guid optionId in new[] { seeded.FirstOptionId, seeded.SecondOptionId })
        {
            HttpResponseMessage guestVote = await AntiforgeryTestClient.SendAsync(
                guestClient,
                HttpMethod.Put,
                $"/api/v1/guest/{seeded.GuestLinkId:N}/poll/options/{optionId:D}/vote",
                JsonContent.Create(new { choice = "available" }),
                guestCookie);
            Assert.Equal(HttpStatusCode.OK, guestVote.StatusCode);
        }

        HttpResponseMessage upgrade = await AntiforgeryTestClient.PostAsync(
            session.Client,
            $"/api/v1/guest/{seeded.GuestLinkId:N}/poll/upgrade",
            content: null,
            $"{guestCookie}; {session.AuthCookie}");
        Assert.Equal(HttpStatusCode.OK, upgrade.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        PollId typedPollId = PollId.From(seeded.PollId);

        // The account keeps exactly the one vote it cast itself; the guest's contradicting vote and
        // its vote on the second option are gone from the database, not merely detached.
        PollVote kept = await context.PollVotes
            .SingleAsync(v => v.PollId == typedPollId && v.UserId == account.Id);
        Assert.Equal(PollOptionId.From(seeded.FirstOptionId), kept.OptionId);
        Assert.Equal(VoteChoice.Unavailable, kept.Choice);
        Assert.Empty(context.PollVotes.Where(v => v.PollId == typedPollId && v.GuestIdentityId != null));
        Assert.Empty(context.PollParticipants.Where(p => p.PollId == typedPollId && p.GuestIdentityId != null));
        Assert.Single(context.PollParticipants.Where(p => p.PollId == typedPollId && p.UserId == account.Id));

        // The identity row itself survives with its provenance; only its roster rows left.
        GuestIdentity identity = await context.GuestIdentities.SingleAsync(g => g.PollId == typedPollId);
        Assert.Equal(account.Id, identity.UpgradedToUserId);
        GuestLink link = await context.GuestLinks.SingleAsync(l => l.Id == GuestLinkId.From(seeded.GuestLinkId));
        Assert.Equal(GuestLinkState.Upgraded, link.State);
    }

    /// <summary>
    /// D-225's terminal-poll refusal: a finalized poll's live, claimed guest cookie is answered
    /// <c>poll_terminal</c> rather than falling through to the generic 404 arm — the seeding mirrors
    /// <see cref="PollProblemCodeClosureTests.PollTerminal_DeletingAnOptionAfterFinalization_Is409"/>.
    /// </summary>
    [Fact]
    public async Task Upgrade_OnAFinalizedPoll_Is409PollTerminal_D225()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-upgrade-term-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);

        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Guest upgrade terminal poll",
                options = new[]
                {
                    new { startTime = SessionEndpointTestSupport.Iso(baseTime) },
                    new { startTime = SessionEndpointTestSupport.Iso(baseTime + Duration.FromHours(1)) },
                },
                visibility = "private",
                minAttendance = 1,
                guestLinkCount = 1,
                inviteeUserIds = Array.Empty<Guid>(),
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = body.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();
        Guid guestLinkId = body.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();
        string url = body.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string token = url[(url.LastIndexOf("/g/", StringComparison.Ordinal) + 3)..];

        using HttpClient guestClient = CreateGuestClient();
        string guestCookie = await ExchangeAsync(guestClient, token);
        await ClaimAsync(guestClient, guestLinkId, guestCookie, "Robin");

        _factory.Clock.Advance(Duration.FromHours(25));
        HttpResponseMessage finalize = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/polls/{pollId:D}/finalize", JsonContent.Create(new { optionId }));
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);

        SessionHttpUser account = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, account.Id);

        HttpResponseMessage upgrade = await AntiforgeryTestClient.PostAsync(
            session.Client,
            $"/api/v1/guest/{guestLinkId:N}/poll/upgrade",
            content: null,
            $"{guestCookie}; {session.AuthCookie}");

        await SessionEndpointTestSupport.AssertProblemAsync(
            upgrade, HttpStatusCode.Conflict, "poll_terminal");
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
        SeededGuestPoll seeded = await SeedPollWithGuestLinkAsync(invitee: null);
        return (seeded.PollId, seeded.FirstOptionId, seeded.GuestLinkId, seeded.Token);
    }

    private async Task<SeededGuestPoll> SeedPollWithGuestLinkAsync(UserId? invitee)
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-upgrade-{Guid.NewGuid():N}@example.test");
        if (invitee is { } inviteeId)
        {
            await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, inviteeId);
        }

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
                inviteeUserIds = invitee is { } id ? new[] { id.Value } : [],
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = body.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();
        Guid guestLinkId = body.RootElement.GetProperty("guestLinks")[0].GetProperty("guestLinkId").GetGuid();
        string url = body.RootElement.GetProperty("guestLinks")[0].GetProperty("url").GetString()!;
        string token = url[(url.LastIndexOf("/g/", StringComparison.Ordinal) + 3)..];
        Guid secondOptionId = body.RootElement.GetProperty("poll").GetProperty("options")[1].GetProperty("id").GetGuid();

        return new SeededGuestPoll(pollId, optionId, secondOptionId, guestLinkId, token);
    }

    private sealed record SeededGuestPoll(
        Guid PollId,
        Guid FirstOptionId,
        Guid SecondOptionId,
        Guid GuestLinkId,
        string Token);
}
