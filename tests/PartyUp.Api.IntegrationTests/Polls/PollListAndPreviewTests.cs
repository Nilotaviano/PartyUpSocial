using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Jobs;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Polls;

/// <summary>
/// ticket-P6-S4c: <c>GET /polls</c>'s two bands (PHASE6_DESIGN.md §4.1), <c>GET /{id}/finalization-preview</c>
/// (§4, design §5 "Preview guarantee"), and the property that ties them together -- preview and
/// finalize share one allocation function, so a preview taken and then finalized on the same option
/// against unchanged inputs must produce an identical roster/waitlist split.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PollListAndPreviewTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    // ---- involved band ----------------------------------------------------------------------------

    [Fact]
    public async Task Involved_ShowsOrganizerAndParticipant_HidesAStranger()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-inv-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"pl-inv-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"pl-inv-stranger-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);

        using JsonDocument organizerList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.Contains(
            organizerList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        using JsonDocument friendList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await friendSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.Contains(
            friendList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        using JsonDocument strangerList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await strangerSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.DoesNotContain(
            strangerList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    [Fact]
    public async Task Involved_ExcludesCanceledAndFinalized()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-inv-term-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        Guid canceledPollId = await CreatePollAsync(organizerSession, []);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{canceledPollId:D}/cancel")).StatusCode);

        (Guid finalizedPollId, Guid optionId) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(25));
        Assert.Equal(
            HttpStatusCode.OK,
            (await organizerSession.SendUnsafeAsync(
                HttpMethod.Post, $"/api/v1/polls/{finalizedPollId:D}/finalize", JsonContent.Create(new { optionId }))).StatusCode);

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));
        var ids = list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        Assert.DoesNotContain(canceledPollId, ids);
        Assert.DoesNotContain(finalizedPollId, ids);
    }

    /// <summary>round-1 note 5: Expired is a terminal status like Canceled/Finalized -- excluded from involved.</summary>
    [Fact]
    public async Task Involved_ExcludesAnExpiredPoll()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-inv-exp-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        _factory.Clock.Advance(Duration.FromHours(25) + Duration.FromHours(48) + Duration.FromMinutes(1));
        await RunPollDueSweepAsync();

        using JsonDocument list = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.DoesNotContain(
            list.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    /// <summary>
    /// round-1 note 1: §7 rule 1's pending-JoinRequest arm (<c>VisibilityEngine.cs:32</c>) was missing
    /// from the poll SQL -- a discoverer who requests to join a poll must move from discoverable to
    /// involved, not vanish from both bands, and must move back on withdrawal.
    /// </summary>
    [Fact]
    public async Task PendingJoinRequest_MovesThePollFromDiscoverableToInvolved_AndBackOnWithdraw()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-pending-org-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"pl-pending-invited-{Guid.NewGuid():N}@example.test");
        UserId requesterId = await _factory.SeedUserAsync(email: $"pl-pending-req-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, invitedFriendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, requesterId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreateNonPrivatePollAsync(organizerSession, invitedFriendId);

        using SessionHttpSession requesterSession = await SessionEndpointTestSupport.SignInAsync(_factory, requesterId);

        using JsonDocument beforeDiscoverable = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.Contains(
            beforeDiscoverable.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        Assert.Equal(
            HttpStatusCode.Created,
            (await requesterSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/join-requests")).StatusCode);

        using JsonDocument afterRequestInvolved = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.Contains(
            afterRequestInvolved.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using JsonDocument afterRequestDiscoverable = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            afterRequestDiscoverable.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await requesterSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/join-requests/mine")).StatusCode);

        using JsonDocument afterWithdrawInvolved = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.DoesNotContain(
            afterWithdrawInvolved.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using JsonDocument afterWithdrawDiscoverable = await SessionEndpointTestSupport.SuccessJsonAsync(
            await requesterSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.Contains(
            afterWithdrawDiscoverable.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    /// <summary>
    /// round-1 note 1: a permanently <c>Removed</c> participant (§12-24, block-then-unblock) fails
    /// rule 1 but is re-admitted by rule 2 through a still-Active, non-all-Unavailable extender --
    /// mirrors <c>VisibilityEngine.Resolve</c>'s answer exactly: discoverable, never involved.
    /// </summary>
    [Fact]
    public async Task RemovedParticipant_WithACleanExtender_IsDiscoverableNotInvolved()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-removed-org-{Guid.NewGuid():N}@example.test");
        UserId removedVoterId = await _factory.SeedUserAsync(email: $"pl-removed-voter-{Guid.NewGuid():N}@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: $"pl-removed-extender-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, removedVoterId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, extenderId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, removedVoterId, extenderId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [removedVoterId, extenderId]);

        string removedVoterUsername = $"removedpoll{Guid.NewGuid():N}"[..20];
        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = claimScope.ServiceProvider.GetRequiredService<AppDbContext>();
            User removedVoterUser = await context.DomainUsers.SingleAsync(u => u.Id == removedVoterId);
            Username username = Username.Create(removedVoterUsername);
            removedVoterUser.ChangeUsername(username, _factory.Clock);
            context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(username, removedVoterId));
            await context.SaveChangesAsync();
        }

        HttpResponseMessage blockCreated = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post, "/api/v1/blocks", JsonContent.Create(new { targetUsername = removedVoterUsername }));
        Assert.Equal(HttpStatusCode.Created, blockCreated.StatusCode);
        using JsonDocument blockBody = await SessionEndpointTestSupport.SuccessJsonAsync(blockCreated);
        Guid blockId = blockBody.RootElement.GetProperty("blockId").GetGuid();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Delete, $"/api/v1/blocks/{blockId:D}")).StatusCode);

        using SessionHttpSession removedVoterSession = await SessionEndpointTestSupport.SignInAsync(_factory, removedVoterId);

        using JsonDocument involved = await SessionEndpointTestSupport.SuccessJsonAsync(
            await removedVoterSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.DoesNotContain(
            involved.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using JsonDocument discoverable = await SessionEndpointTestSupport.SuccessJsonAsync(
            await removedVoterSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.Contains(
            discoverable.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    // ---- discoverable band --------------------------------------------------------------------------

    [Fact]
    public async Task Discoverable_ShowsANonPrivatePollThroughAFriendOfTheOrganizer_HidesAPrivateOne()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-disc-org-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"pl-disc-invited-{Guid.NewGuid():N}@example.test");
        UserId discovererId = await _factory.SeedUserAsync(email: $"pl-disc-friend-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"pl-disc-stranger-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, invitedFriendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, discovererId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid nonPrivatePollId = await CreateNonPrivatePollAsync(organizerSession, invitedFriendId);
        Guid privatePollId = await CreatePollAsync(organizerSession, []);

        // The rule-2 discoverer is a friend of the organizer but never invited (unattached) --
        // exactly the D-118 entry point, distinct from the already-attached invitee.
        using SessionHttpSession discovererSession = await SessionEndpointTestSupport.SignInAsync(_factory, discovererId);
        using JsonDocument discovererList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await discovererSession.GetAsync("/api/v1/polls?band=discoverable"));
        var discovererIds = discovererList.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(nonPrivatePollId, discovererIds);
        Assert.DoesNotContain(privatePollId, discovererIds);

        // The invited friend is already attached (rule-1) -- discover excludes them the same way
        // ListDiscoverAsync excludes an already-attached session participant.
        using SessionHttpSession invitedSession = await SessionEndpointTestSupport.SignInAsync(_factory, invitedFriendId);
        using JsonDocument invitedList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await invitedSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            invitedList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == nonPrivatePollId);

        using SessionHttpSession strangerSession = await SessionEndpointTestSupport.SignInAsync(_factory, strangerId);
        using JsonDocument strangerList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await strangerSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            strangerList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == nonPrivatePollId);
    }

    [Fact]
    public async Task Discoverable_ExcludesAClosedPoll()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-disc-closed-org-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"pl-disc-closed-invited-{Guid.NewGuid():N}@example.test");
        UserId discovererId = await _factory.SeedUserAsync(email: $"pl-disc-closed-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, invitedFriendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, discovererId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreateNonPrivatePollAsync(organizerSession, invitedFriendId);
        _factory.Clock.Advance(Duration.FromHours(25));
        await RunPollDueSweepAsync();

        using SessionHttpSession discovererSession = await SessionEndpointTestSupport.SignInAsync(_factory, discovererId);
        using JsonDocument discovererList = await SessionEndpointTestSupport.SuccessJsonAsync(
            await discovererSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            discovererList.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    /// <summary>round-1 note 5: rule 4 (blocked pair) beats rule 2 -- a blocked viewer sees the poll in neither band.</summary>
    [Fact]
    public async Task Discoverable_ExcludesAPollWhoseOrganizerBlockedTheViewer()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-disc-blocked-org-{Guid.NewGuid():N}@example.test");
        UserId invitedFriendId = await _factory.SeedUserAsync(email: $"pl-disc-blocked-invited-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"pl-disc-blocked-viewer-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, invitedFriendId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, viewerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreateNonPrivatePollAsync(organizerSession, invitedFriendId);

        string viewerUsername = $"discblocked{Guid.NewGuid():N}"[..20];
        await using (AsyncServiceScope claimScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = claimScope.ServiceProvider.GetRequiredService<AppDbContext>();
            User viewerUser = await context.DomainUsers.SingleAsync(u => u.Id == viewerId);
            Username username = Username.Create(viewerUsername);
            viewerUser.ChangeUsername(username, _factory.Clock);
            context.UsernameRegistry.Add(UsernameRegistryEntry.ClaimNew(username, viewerId));
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            HttpStatusCode.Created,
            (await organizerSession.SendUnsafeAsync(
                HttpMethod.Post, "/api/v1/blocks", JsonContent.Create(new { targetUsername = viewerUsername }))).StatusCode);

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        using JsonDocument involved = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/polls?band=involved"));
        Assert.DoesNotContain(
            involved.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        using JsonDocument discoverable = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            discoverable.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    /// <summary>
    /// round-1 note 5 / §12-17: the only extender voting Unavailable on every option does not extend
    /// access -- a friend-of-extender-only viewer cannot discover the poll -- but revising a single
    /// vote off all-Unavailable restores discoverability.
    /// </summary>
    [Fact]
    public async Task Discoverable_ExcludesWhileTheOnlyExtenderVotedUnavailableThroughout_ReincludedOnRevision()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-disc-unavail-org-{Guid.NewGuid():N}@example.test");
        UserId extenderId = await _factory.SeedUserAsync(email: $"pl-disc-unavail-ext-{Guid.NewGuid():N}@example.test");
        UserId viewerId = await _factory.SeedUserAsync(email: $"pl-disc-unavail-viewer-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, extenderId);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, extenderId, viewerId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        (Guid pollId, Guid firstOptionId) = await CreatePollWithFirstOptionAsync(organizerSession, [extenderId]);
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Guid secondOptionId = detail.RootElement.GetProperty("options")[1].GetProperty("id").GetGuid();

        using SessionHttpSession extenderSession = await SessionEndpointTestSupport.SignInAsync(_factory, extenderId);
        Assert.Equal(
            HttpStatusCode.OK,
            (await extenderSession.SendUnsafeAsync(
                HttpMethod.Put, $"/api/v1/polls/{pollId:D}/options/{firstOptionId:D}/vote", JsonContent.Create(new { choice = "unavailable" })))
                .StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await extenderSession.SendUnsafeAsync(
                HttpMethod.Put, $"/api/v1/polls/{pollId:D}/options/{secondOptionId:D}/vote", JsonContent.Create(new { choice = "unavailable" })))
                .StatusCode);

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        using JsonDocument beforeRevision = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.DoesNotContain(
            beforeRevision.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);

        Assert.Equal(
            HttpStatusCode.OK,
            (await extenderSession.SendUnsafeAsync(
                HttpMethod.Put, $"/api/v1/polls/{pollId:D}/options/{firstOptionId:D}/vote", JsonContent.Create(new { choice = "available" })))
                .StatusCode);

        using JsonDocument afterRevision = await SessionEndpointTestSupport.SuccessJsonAsync(
            await viewerSession.GetAsync("/api/v1/polls?band=discoverable"));
        Assert.Contains(
            afterRevision.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == pollId);
    }

    // ---- cursor ----------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidBand_Is400()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"pl-band-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await session.GetAsync("/api/v1/polls?band=nonsense"),
            HttpStatusCode.BadRequest,
            "poll_validation");
    }

    [Fact]
    public async Task CrossBandCursor_IsRejected()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-cursor-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        await CreatePollAsync(organizerSession, []);

        using JsonDocument involvedPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));

        // A page short of the fixed page size mints no next cursor -- seed one directly against the
        // shared SessionListCursor machinery (SessionListScope.PollsInvolved) the same way the
        // session list's own cross-scope test does, rather than paging through twenty polls.
        string involvedCursor = new PartyUp.Application.Sessions.SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), PartyUp.Application.Sessions.SessionListScope.PollsInvolved).Encode();

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls?band=discoverable&cursor={Uri.EscapeDataString(involvedCursor)}"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");

        Assert.True(involvedPage.RootElement.TryGetProperty("nextCursor", out _));
    }

    /// <summary>round-1 note 4: a cursor minted for an entirely different list feature (session Discover) is rejected too.</summary>
    [Fact]
    public async Task CrossFeatureCursor_SessionDiscoverCursorIsRejectedOnThePollRoute()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"pl-cursor-xfeature-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);

        string sessionDiscoverCursor = new PartyUp.Application.Sessions.SessionListCursor(
            _factory.Clock.GetCurrentInstant(), Guid.NewGuid(), PartyUp.Application.Sessions.SessionListScope.Discover).Encode();

        await SessionEndpointTestSupport.AssertProblemAsync(
            await session.GetAsync($"/api/v1/polls?band=involved&cursor={Uri.EscapeDataString(sessionDiscoverCursor)}"),
            HttpStatusCode.BadRequest,
            "invalid_cursor");
    }

    /// <summary>round-1 note 4: a full page-boundary walk -- no duplicate and no skipped poll across the cursor seam.</summary>
    [Fact]
    public async Task Involved_TwentyOnePolls_PagesAcrossTheBoundaryWithNoDuplicateOrSkip()
    {
        const int totalPolls = 21;
        const int pageSize = 20;

        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-cursor-walk-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        HashSet<Guid> created = [];
        for (int i = 0; i < totalPolls; i++)
        {
            created.Add(await CreatePollAsync(organizerSession, []));
            _factory.Clock.Advance(Duration.FromMinutes(1)); // distinct deadlines -> deterministic (deadline, id) order
        }

        using JsonDocument firstPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync("/api/v1/polls?band=involved"));
        Guid[] firstPageIds = [.. firstPage.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
        Assert.Equal(pageSize, firstPageIds.Length);
        string? nextCursor = firstPage.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(nextCursor);

        using JsonDocument secondPage = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls?band=involved&cursor={Uri.EscapeDataString(nextCursor!)}"));
        Guid[] secondPageIds = [.. secondPage.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
        Assert.Single(secondPageIds); // totalPolls (21) - pageSize (20) == 1
        Assert.Null(secondPage.RootElement.GetProperty("nextCursor").GetString());

        Assert.Empty(firstPageIds.Intersect(secondPageIds)); // no duplicate across the seam
        Assert.Equal(created.OrderBy(id => id), firstPageIds.Concat(secondPageIds).OrderBy(id => id)); // no skip
    }

    // ---- finalization preview ---------------------------------------------------------------------

    /// <summary>
    /// D-187: the preview is what tells the organizer which option is already viable, so it is
    /// readable while the poll is still Polling -- it closes only with the selection window.
    /// </summary>
    [Fact]
    public async Task Preview_PreDeadlineIs200_InWindowIs200_PostWindowIs422_D187()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-window-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        (Guid earlyPollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        HttpResponseMessage earlyPreview = await organizerSession.GetAsync(
            $"/api/v1/polls/{earlyPollId:D}/finalization-preview");
        Assert.Equal(HttpStatusCode.OK, earlyPreview.StatusCode);
        using JsonDocument earlyBody = await SessionEndpointTestSupport.SuccessJsonAsync(earlyPreview);
        JsonElement earlyOptions = earlyBody.RootElement.GetProperty("options");
        Assert.Equal(2, earlyOptions.GetArrayLength());
        // minAttendance 1 and the organizer always Going (D-155): both options already qualify.
        Assert.All(
            earlyOptions.EnumerateArray(),
            option => Assert.True(option.GetProperty("meetsMinimum").GetBoolean()));

        (Guid onTimePollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(25));
        HttpResponseMessage onTimePreview = await organizerSession.GetAsync(
            $"/api/v1/polls/{onTimePollId:D}/finalization-preview");
        Assert.Equal(HttpStatusCode.OK, onTimePreview.StatusCode);
        using JsonDocument onTimeBody = await SessionEndpointTestSupport.SuccessJsonAsync(onTimePreview);
        Assert.Equal(2, onTimeBody.RootElement.GetProperty("options").GetArrayLength());

        (Guid latePollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(73));
        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{latePollId:D}/finalization-preview"),
            HttpStatusCode.UnprocessableEntity,
            "poll_selection_window_closed");
    }

    [Fact]
    public async Task Preview_NonOrganizerIsPollNotFound()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-nf-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"pl-preview-nf-friend-{Guid.NewGuid():N}@example.test");
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, friendId);

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, [friendId]);
        _factory.Clock.Advance(Duration.FromHours(25));

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friendId);
        await SessionEndpointTestSupport.AssertProblemAsync(
            await friendSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"),
            HttpStatusCode.NotFound,
            "poll_not_found");
    }

    /// <summary>round-1 note 2: a terminal poll (Canceled/Expired/Finalized) short-circuits to poll_invalid_transition, never a hollow 200.</summary>
    [Fact]
    public async Task Preview_ACanceledZeroOptionPoll_IsPollInvalidTransition()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-term-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        Guid pollId = await CreatePollAsync(organizerSession, []);

        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        foreach (JsonElement option in detail.RootElement.GetProperty("options").EnumerateArray())
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await organizerSession.SendUnsafeAsync(
                    HttpMethod.Delete, $"/api/v1/polls/{pollId:D}/options/{option.GetProperty("id").GetGuid():D}")).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await organizerSession.SendUnsafeAsync(HttpMethod.Post, $"/api/v1/polls/{pollId:D}/cancel")).StatusCode);

        await SessionEndpointTestSupport.AssertProblemAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"),
            HttpStatusCode.Conflict,
            "poll_invalid_transition");
    }

    /// <summary>
    /// round-1 note 6: preview never persists. A poll due-but-unswept (deadline passed, no write path
    /// has applied the due transition yet) still previews 200 -- but the materialized status column
    /// stays exactly what it was, and no notification row or session row is created by the call.
    /// </summary>
    [Fact]
    public async Task Preview_IsSideEffectFree_OnADueButUnsweptPoll()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-sideeffect-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        (Guid pollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);
        _factory.Clock.Advance(Duration.FromHours(25));

        PollStatus statusBefore;
        int notificationCountBefore;
        int sessionCountBefore;
        await using (AsyncServiceScope beforeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            statusBefore = (await context.Polls.AsNoTracking().SingleAsync(p => p.Id == PollId.From(pollId))).Status;
            notificationCountBefore = await context.Notifications.CountAsync();
            sessionCountBefore = await context.Sessions.CountAsync();
        }

        Assert.Equal(PollStatus.Polling, statusBefore); // due but not yet swept by any write path

        HttpResponseMessage preview = await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);

        await using (AsyncServiceScope afterScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
            PollStatus statusAfter = (await context.Polls.AsNoTracking().SingleAsync(p => p.Id == PollId.From(pollId))).Status;
            int notificationCountAfter = await context.Notifications.CountAsync();
            int sessionCountAfter = await context.Sessions.CountAsync();

            Assert.Equal(statusBefore, statusAfter); // preview never persists the inline due-Close it simulates
            Assert.Equal(notificationCountBefore, notificationCountAfter);
            Assert.Equal(sessionCountBefore, sessionCountAfter);
        }
    }

    /// <summary>
    /// The preview guarantee (design §5): calling preview then finalizing the same option, against
    /// unchanged inputs, must yield the identical roster/waitlist split IN ALLOCATION ORDER (D-119) --
    /// because both routes call the same <c>Poll.Finalize</c> allocation, over unchanged aggregate
    /// state, the natural (never re-sorted) order out of each response must already agree. Capacity 3
    /// against organizer + 2 Available + 2 Maybe puts the D-119 waitlist split in play: the two Maybe
    /// voters overflow.
    /// </summary>
    [Fact]
    public async Task Preview_MatchesTheFinalizedSessionRoster_InAllocationOrder_WithAWaitlistSplit()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-eq-org-{Guid.NewGuid():N}@example.test");
        UserId avail1Id = await _factory.SeedUserAsync(email: $"pl-preview-eq-avail1-{Guid.NewGuid():N}@example.test");
        UserId avail2Id = await _factory.SeedUserAsync(email: $"pl-preview-eq-avail2-{Guid.NewGuid():N}@example.test");
        UserId maybe1Id = await _factory.SeedUserAsync(email: $"pl-preview-eq-maybe1-{Guid.NewGuid():N}@example.test");
        UserId maybe2Id = await _factory.SeedUserAsync(email: $"pl-preview-eq-maybe2-{Guid.NewGuid():N}@example.test");
        foreach (UserId inviteeId in new[] { avail1Id, avail2Id, maybe1Id, maybe2Id })
        {
            await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizerId, inviteeId);
        }

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Waitlist-split preview fixture",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                isPrivate = false,
                minAttendance = 1,
                maxCapacity = 3,
                inviteeUserIds = new[] { avail1Id.Value, avail2Id.Value, maybe1Id.Value, maybe2Id.Value },
            }));
        using JsonDocument createdBody = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid pollId = createdBody.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
        Guid optionId = createdBody.RootElement.GetProperty("poll").GetProperty("options")[0].GetProperty("id").GetGuid();

        async Task VoteAsync(UserId voterId, string choice)
        {
            using SessionHttpSession voterSession = await SessionEndpointTestSupport.SignInAsync(_factory, voterId);
            Assert.Equal(
                HttpStatusCode.OK,
                (await voterSession.SendUnsafeAsync(
                    HttpMethod.Put, $"/api/v1/polls/{pollId:D}/options/{optionId:D}/vote", JsonContent.Create(new { choice }))).StatusCode);
        }

        await VoteAsync(avail1Id, "available");
        await VoteAsync(avail2Id, "available");
        await VoteAsync(maybe1Id, "maybe");
        await VoteAsync(maybe2Id, "maybe");

        _factory.Clock.Advance(Duration.FromHours(25));

        using JsonDocument previewBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"));
        JsonElement previewOption = previewBody.RootElement.GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("optionId").GetGuid() == optionId);
        var previewRoster = previewOption.GetProperty("roster").EnumerateArray()
            .Where(r => r.TryGetProperty("userId", out _))
            .Select(r => (UserId: r.GetProperty("userId").GetGuid(), Status: r.GetProperty("status").GetString(), IsWaitlisted: r.GetProperty("isWaitlisted").GetBoolean()))
            .ToArray(); // natural (API) order -- never re-sorted

        // D-119: organizer, then Available voters, then Maybe voters -- capacity 3 waitlists both Maybes.
        Assert.Equal(5, previewRoster.Length);
        Assert.Equal((organizerId.Value, "going", false), previewRoster[0]);
        Assert.Contains(previewRoster[1..3], r => r.UserId == avail1Id.Value && r.Status == "going" && !r.IsWaitlisted);
        Assert.Contains(previewRoster[1..3], r => r.UserId == avail2Id.Value && r.Status == "going" && !r.IsWaitlisted);
        Assert.Contains(previewRoster[3..5], r => r.UserId == maybe1Id.Value && r.Status == "maybe" && r.IsWaitlisted);
        Assert.Contains(previewRoster[3..5], r => r.UserId == maybe2Id.Value && r.Status == "maybe" && r.IsWaitlisted);

        HttpResponseMessage finalize = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post, $"/api/v1/polls/{pollId:D}/finalize", JsonContent.Create(new { optionId }));
        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
        using JsonDocument finalizeBody = await SessionEndpointTestSupport.SuccessJsonAsync(finalize);
        Guid sessionId = finalizeBody.RootElement.GetProperty("sessionId").GetGuid();

        using JsonDocument sessionBody = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/sessions/{sessionId:D}"));
        var finalizedRoster = sessionBody.RootElement.GetProperty("roster").EnumerateArray()
            .Where(r => r.TryGetProperty("userId", out _))
            .Select(r => (UserId: r.GetProperty("userId").GetGuid(), Status: r.GetProperty("status").GetString(), IsWaitlisted: r.GetProperty("isWaitlisted").GetBoolean()))
            .ToArray();

        // Content equality, not sequence equality: GET /sessions/{id}'s roster comes back through a
        // second DB round trip (SessionReadRepository.ToDetailAsync over an EF collection navigation
        // with no ORDER BY) -- that route makes no ordering promise and none is asserted on it
        // elsewhere in the suite. The allocation-order claim this test actually verifies is checked
        // directly above, against the preview response's own array (which -- unlike the session
        // detail read -- is emitted straight from the in-memory AllocateRoster/FromPollFinalization
        // list Poll.Finalize built, with no intervening query). The preview-equals-finalize property
        // (design §5) is a same-inputs-same-outputs claim over the roster/waitlist split itself, not
        // over an unrelated endpoint's incidental wire order.
        Assert.Equal(previewRoster.OrderBy(r => r.UserId), finalizedRoster.OrderBy(r => r.UserId));
    }

    /// <summary>
    /// D-188: the preview roster is organizer-only to reach, but a registered row can still be a
    /// blocked counterpart (D-076/D-086 test below), so this only proves the unmasked case -- every
    /// unmasked registered row carries the participant's avatar, resolved from the same prefetched
    /// nameplate dictionary the display name comes from, not a second lookup.
    /// </summary>
    [Fact]
    public async Task PreviewRosterEntriesCarryTheParticipantAvatar_D188()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-avatar-{Guid.NewGuid():N}@example.test");
        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);

        (Guid pollId, _) = await CreatePollWithFirstOptionAsync(organizerSession);

        using JsonDocument preview = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"));

        JsonElement organizerRow = preview.RootElement.GetProperty("options")[0].GetProperty("roster")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("userId").GetGuid() == organizerId.Value);
        JsonElement avatar = organizerRow.GetProperty("avatar");
        Assert.Equal("emoji", avatar.GetProperty("kind").GetString());
        Assert.Equal("🙂", avatar.GetProperty("emoji").GetString());
    }

    /// <summary>
    /// D-076/D-086: masking is presentation, not removal, so a participant can be blocked with the
    /// organizer while remaining Active on the poll (built directly against the aggregate here,
    /// bypassing <c>BlockUserUseCase</c>'s effects executor -- exactly what <c>SessionRosterMaskingWireTests</c>
    /// does for sessions -- so the Blocks row exists without the block command's own side effect of
    /// removing the participant). The same organizer sees this participant masked on
    /// <c>GET /polls/{id}</c> (PollReadUseCases), so the preview -- reachable for the same person on
    /// the same poll -- must mask identically: neither <c>displayName</c> nor <c>avatar</c> present on
    /// the wire.
    /// </summary>
    [Fact]
    public async Task Preview_BlockedCounterpartRow_CarriesNoDisplayNameOrAvatar_D076()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"pl-preview-blocked-org-{Guid.NewGuid():N}@example.test");
        UserId blockedFriendId = await _factory.SeedUserAsync(email: $"pl-preview-blocked-friend-{Guid.NewGuid():N}@example.test");

        Guid pollId;
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Poll poll = Poll.Create(
                organizerId,
                new PollDetails(
                    SessionTitle.Create("Blocked-counterpart preview fixture"),
                    Duration15.FromMinutes(60),
                    Platform.NotApplicable,
                    null,
                    SessionNotes.Empty,
                    minAttendance: 1,
                    maxCapacity: null,
                    visibility: ContentVisibility.Public),
                PollDeadlineChoice.Hours24,
                [
                    _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
                    _factory.Clock.GetCurrentInstant() + Duration.FromDays(2),
                ],
                [blockedFriendId],
                _factory.Clock);
            _ = poll.DequeueEvents();
            pollId = poll.Id.Value;

            context.Polls.Add(poll);
            await context.SaveChangesAsync();

            User blockedFriendUser = await context.DomainUsers.SingleAsync(u => u.Id == blockedFriendId);
            context.Blocks.Add(Block.Create(
                organizerId, blockedFriendId, blockedFriendUser.Username!.Value, _factory.Clock));
            await context.SaveChangesAsync();
        }

        using SessionHttpSession organizerSession = await SessionEndpointTestSupport.SignInAsync(_factory, organizerId);
        using JsonDocument preview = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}/finalization-preview"));

        JsonElement organizerRow = preview.RootElement.GetProperty("options")[0].GetProperty("roster")
            .EnumerateArray()
            .Single(entry => entry.TryGetProperty("userId", out JsonElement id) && id.GetGuid() == organizerId.Value);
        Assert.True(organizerRow.TryGetProperty("displayName", out _));
        Assert.True(organizerRow.TryGetProperty("avatar", out _));

        JsonElement blockedRow = preview.RootElement.GetProperty("options")[0].GetProperty("roster")
            .EnumerateArray()
            .Single(entry => !entry.TryGetProperty("userId", out _));
        Assert.False(blockedRow.TryGetProperty("displayName", out _));
        Assert.False(blockedRow.TryGetProperty("avatar", out _));
    }

    // ---- fixture helpers ----------------------------------------------------------------------------

    /// <summary>
    /// The materialized status column only flips on a write path or the due-sweep job -- a plain list
    /// read never re-derives it (ARCHITECTURE's materialized-status invariant) -- so tests that need a
    /// due poll to actually read as Closed/Expired run the sweep directly, the same way
    /// <c>PollSweepTests</c> does.
    /// </summary>
    private async Task RunPollDueSweepAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<PollDueSweepJob>();
        await job.ExecuteAsync(default);
    }

    private async Task<Guid> CreatePollAsync(SessionHttpSession organizerSession, IReadOnlyList<UserId> inviteeIds)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "List/preview fixture poll",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                isPrivate = inviteeIds.Count == 0,
                minAttendance = 1,
                inviteeUserIds = inviteeIds.Select(id => id.Value).ToArray(),
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    /// <summary>D-125: a non-private poll requires >=1 directly invited registered friend at creation.</summary>
    private async Task<Guid> CreateNonPrivatePollAsync(SessionHttpSession organizerSession, UserId inviteeId)
    {
        HttpResponseMessage created = await organizerSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/polls",
            JsonContent.Create(new
            {
                title = "Discoverable fixture poll",
                optionStartTimes = new[]
                {
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                    SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(2)),
                },
                isPrivate = false,
                minAttendance = 1,
                inviteeUserIds = new[] { inviteeId.Value },
            }));
        using JsonDocument body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        return body.RootElement.GetProperty("poll").GetProperty("id").GetGuid();
    }

    private async Task<(Guid PollId, Guid OptionId)> CreatePollWithFirstOptionAsync(
        SessionHttpSession organizerSession, IReadOnlyList<UserId>? inviteeIds = null)
    {
        Guid pollId = await CreatePollAsync(organizerSession, inviteeIds ?? []);
        using JsonDocument detail = await SessionEndpointTestSupport.SuccessJsonAsync(
            await organizerSession.GetAsync($"/api/v1/polls/{pollId:D}"));
        Guid optionId = detail.RootElement.GetProperty("options")[0].GetProperty("id").GetGuid();
        return (pollId, optionId);
    }
}
