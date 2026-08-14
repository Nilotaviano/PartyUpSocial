using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// The poll detail HTTP response's per-option voter lists (product-ratified results-legibility): each
/// option carries <c>availableVoters</c>/<c>maybeVoters</c>/<c>unavailableVoters</c> alongside the
/// existing counts, and a masked (blocked-pair) voter renders with the same absent <c>displayName</c>
/// and the same <c>participationId</c> in both the roster and every option list it appears in --
/// mirrors <see cref="PollEndpointTests.AMaskedPollRosterEntryHasNoUserIdProperty_D076"/>'s wire
/// precedent, extended to the new voter-list channel (D-076).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollOptionVoterListsWireTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task PollDetail_CarriesPerOptionVoterListsAlongsideCounts()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-voters-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"poll-voters-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);
        Guid optionId = await FirstOptionIdAsync(organizerSession, pollId);

        await VoteAsync(organizerSession, pollId, optionId, "available");
        await VoteAsync(friendSession, pollId, optionId, "maybe");

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        JsonElement option = detail.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("id").GetGuid() == optionId);

        Assert.Equal(1, option.GetProperty("availableCount").GetInt32());
        Assert.Equal(1, option.GetProperty("maybeCount").GetInt32());
        Assert.Equal(0, option.GetProperty("unavailableCount").GetInt32());

        JsonElement availableVoters = option.GetProperty("availableVoters");
        Assert.Equal(1, availableVoters.GetArrayLength());
        Assert.Equal(organizerId.Value, availableVoters[0].GetProperty("userId").GetGuid());

        JsonElement maybeVoters = option.GetProperty("maybeVoters");
        Assert.Equal(1, maybeVoters.GetArrayLength());
        Assert.Equal(friendId.Value, maybeVoters[0].GetProperty("userId").GetGuid());

        Assert.Equal(0, option.GetProperty("unavailableVoters").GetArrayLength());
    }

    [Fact]
    public async Task PollDetail_MaskedVoter_HasNullDisplayNameAndSameOrdinalInRosterAndOptionList_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"poll-voters-mask-org-{Guid.NewGuid():N}@example.test");
        UserId blockedId = await _factory.SeedUserAsync(email: $"poll-voters-mask-blocked-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, blockedId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using SessionHttpSession blockedSession = await SessionEndpointTestSupport.SignInAsync(_factory, blockedId);
        Guid pollId = await CreatePollAsync(organizerSession, [blockedId]);
        Guid optionId = await FirstOptionIdAsync(organizerSession, pollId);

        await VoteAsync(blockedSession, pollId, optionId, "available");

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            User blockedUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedId);
            context.Blocks.Add(Block.Create(organizerId, blockedId, blockedUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));

        JsonElement roster = detail.RootElement.GetProperty("roster");
        JsonElement maskedRosterEntry = roster.EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _) && !entry.TryGetProperty("displayName", out _));
        Guid maskedParticipationId = maskedRosterEntry.GetProperty("participationId").GetGuid();

        JsonElement option = detail.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("id").GetGuid() == optionId);
        JsonElement availableVoters = option.GetProperty("availableVoters");
        Assert.Equal(1, availableVoters.GetArrayLength());
        JsonElement maskedVoterEntry = availableVoters[0];

        Assert.False(maskedVoterEntry.TryGetProperty("userId", out _));
        Assert.False(maskedVoterEntry.TryGetProperty("displayName", out _));
        Assert.False(maskedVoterEntry.TryGetProperty("avatar", out _));
        Assert.Equal(maskedParticipationId, maskedVoterEntry.GetProperty("participationId").GetGuid());
    }

    private async Task<Guid> CreatePollAsync(SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Voter list poll fixture",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> FirstOptionIdAsync(SessionHttpSession session, Guid pollId)
    {
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await session.GetAsync($"/api/v1/polls/{pollId:D}"));
        return detail.RootElement.GetProperty("options")[0].GetProperty("id").GetGuid();
    }

    private static async Task VoteAsync(SessionHttpSession session, Guid pollId, Guid optionId, string choice)
    {
        HttpResponseMessage response = await session.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote",
            JsonContent.Create(new { choice }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
