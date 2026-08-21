using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Api.ErrorHandling;
using PartyUp.Api.Security;
using PartyUp.Api.IntegrationTests.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Steam;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RateLimitTests : IAsyncLifetime, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedRoutes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["auth-ip"] = ["Login", "ConsumeMagicLink", "ResetPassword", "VerifyEmail"],
            ["login-email"] = ["Login"],
            ["register-ip"] = ["Register"],
            ["register-email"] = ["Register"],
            ["auth-email"] = ["RequestMagicLink", "ForgotPassword"],
            ["magic-link-ip"] = ["RequestMagicLink"],
            ["deletion-request-email"] = ["RequestAccountDeletion"],
            ["deletion-request-ip"] = ["RequestAccountDeletion"],
            ["deletion-steam-ip"] = ["StartSteamDeletion", "CompleteSteamDeletion"],
            ["intent-ip"] = ["IssueExternalIntent"],
            ["email-send"] = ["RequestEmailVerification", "ReauthenticateByEmail"],
            ["reauth"] =
            [
                "Reauthenticate",
                "ReauthenticateByEmail",
                "ChangePassword",
                "SetPassword",
                "IssueExternalIntent",
                "DeleteAuthMethod",
                "DeletePlatformLink",
                "ChangeEmail",
                "DeleteMe",
            ],
            ["lookup"] = ["CheckUsernameAvailability"],
            ["friend-lookup"] =
            [
                "GetUserProfile",
                "ResolveFriendCode",
                "BlockUser",
                "ImportSteamFriends",
            ],
            ["friend-request-send"] = ["SendFriendRequest"],
            ["steam-friend-import"] = ["ImportSteamFriends"],
            ["session-write"] =
            [
                "CreateSession",
                "EditSession",
                "CancelSession",
                "InviteSessionUsers",
                "CreateSessionGuestLinks",
                "RevokeSessionGuestLink",
                "RemoveSessionGuest",
                "RespondToSessionInvitation",
                "LeaveSession",
                "RequestSessionJoin",
                "WithdrawSessionJoinRequest",
                "SetSessionReminder",
            ],
            ["session-attach"] =
            [
                "ApproveSessionJoinRequest",
                "RejectSessionJoinRequest",
                "PromoteSessionWaitlistParticipant",
                "RejectSessionWaitlistParticipant",
            ],
            // D-224: the crawler card shares the exchange's dimension.
            ["guest-link-exchange"] = ["ExchangeGuestLink", "GetGuestLinkUnfurlCard"],
            // AVATARS_DESIGN.md §3.3: sized for page loads, its own dimension so a large friend
            // roster's avatar fan-out never competes with `default`'s API-call ceiling.
            ["avatar-proxy"] = ["GetAvatarProxy"],
            ["guest-write"] =
            [
                "ClaimGuestSessionLink",
                "RenameGuestSessionIdentity",
                "RespondAsGuest",
                // PHASE6_DESIGN.md §4.2 (ticket-P6-S4b): poll guest ops join this dimension.
                "ClaimGuestPollLink",
                "VoteOnPollAsGuest",
                "SuggestPollOptionAsGuest",
                "RenameGuestPollIdentity",
                // D-225: the guest -> account upgrade, session and poll arms.
                "UpgradeGuestSession",
                "UpgradeGuestPoll",
            ],
            // PHASE5_DESIGN.md A-079 (round-2 fix, disclosed extension — Phase 4 S4 precedent for
            // mechanical RateLimitTests/AntiforgeryTests inventory extensions): five new
            // User-partitioned dimensions, assigned exhaustively so §8 has no unassigned route.
            ["availability-write"] =
            [
                "CreateAvailabilityBlock",
                "EditAvailabilityBlock",
                "DeleteAvailabilityBlock",
                "CreateAvailabilitySeries",
                "DeleteAvailabilitySeries",
                "EditAvailabilityOccurrence",
                "DeleteAvailabilityOccurrence",
            ],
            ["overlap-search"] = ["FindOverlapWindows", "GetHomeSuggestions"],
            ["calendar-read"] = ["GetCalendarView", "GetMyAvailability"],
            ["home-read"] = ["GetActionableInbox"],
            ["session-list-read"] = ["ListSessions"],
            // PHASE6_DESIGN.md §4.2 (ticket-P6-S4a): a poll-local grouping, distinct from the
            // session split — the session-write/session-attach analogues.
            ["poll-write"] =
            [
                "CreatePoll",
                "CancelPoll",
                "DeletePollOption",
                "CreatePollGuestLinks",
                "RevokePollGuestLink",
                "RemovePollGuest",
                // D-186: the poll-local analogue of LeaveSession's place in session-write.
                "LeavePoll",
                // D-193: the poll-local analogue of InviteSessionUsers's place in session-write.
                "InvitePollUsers",
            ],
            ["poll-attach"] =
            [
                "FinalizePoll",
                "ApprovePollJoinRequest",
                "RejectPollJoinRequest",
                "RequestPollJoin",
                "WithdrawPollJoinRequest",
                "ListPollJoinRequests",
            ],
            ["poll-vote"] = ["VoteOnPollOption", "SuggestPollOption"],
            ["poll-list-read"] = ["ListPolls", "GetPollFinalizationPreview"],
            // PHASE7_DESIGN.md §4 (ticket-P7-S4): three new User-partitioned dimensions, 30 -> 33.
            ["notification-read"] = ["ListNotifications", "GetUnreadNotificationCount", "GetPushConfig"],
            ["notification-write"] =
            [
                "MarkNotificationRead",
                "MarkAllNotificationsRead",
                "MuteSession",
                "UnmuteSession",
                "MutePoll",
                "UnmutePoll",
            ],
            ["push-subscribe"] = ["CreatePushSubscription", "DeletePushSubscription"],
            // STEAM_CATALOG_DESIGN.md §3: typeahead traffic must not eat the general read budget.
            ["steam-catalog-search"] = ["SearchSteamCatalog"],
            // D-213: the account-less feedback write, bounded per IP and per authenticated user.
            ["feedback-ip"] = ["SubmitFeedback"],
            ["feedback-user"] = ["SubmitFeedback"],
            ["default"] = [],
        };

    private readonly PartyUpApiFactory _factory = new(googleEnabled: true);

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void EveryDimensionCoversItsDocumentedRoutes()
    {
        Assert.Equal(37, RateLimitPolicies.Dimensions.Count);
        Assert.Equal(ExpectedRoutes.Keys, RateLimitPolicies.Dimensions.Select(value => value.Name));
        foreach (RateLimitDimension dimension in RateLimitPolicies.Dimensions)
        {
            Assert.True(
                dimension.OperationIds.SetEquals(ExpectedRoutes[dimension.Name]),
                $"Unexpected route set for {dimension.Name}.");
        }

        Assert.True(RateLimitPolicies.Dimensions[^1].CoversEveryApiOperation);
        IEnumerable<EndpointDataSource> dataSources =
            _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        Assert.DoesNotContain(
            dataSources.SelectMany(source => source.Endpoints),
            endpoint => endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null);
    }

    /// <summary>
    /// AVATARS_DESIGN.md §3.3: <c>avatar-proxy</c>'s wider 600/min ceiling is only real if
    /// <c>default</c> (300/min) does not also throttle <c>GetAvatarProxy</c> — the chained limiter
    /// requires every partition to grant a lease, so the narrower dimension would otherwise be the
    /// one actually observed. <c>default</c> must skip a claimed operation entirely (no limiter, not
    /// merely a wider one), and <c>avatar-proxy</c> must be the dimension actually applying a bucket.
    /// </summary>
    [Fact]
    public void AvatarProxy_IsBoundedByItsOwnDimensionNotTheDefaultCeiling()
    {
        RateLimitDimension defaultDimension = RateLimitPolicies.Dimensions.Single(d => d.Name == "default");
        RateLimitDimension avatarProxy = RateLimitPolicies.Dimensions.Single(d => d.Name == "avatar-proxy");
        byte[] pepper = "test-pepper"u8.ToArray();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new EndpointNameMetadata(ApiOperationIds.GetAvatarProxy)),
            "avatar-proxy-test"));

        RateLimitPartition<string> defaultPartition =
            RateLimitPolicies.PartitionFor(context, defaultDimension, pepper);
        Assert.Equal(string.Empty, defaultPartition.PartitionKey);

        RateLimitPartition<string> avatarProxyPartition =
            RateLimitPolicies.PartitionFor(context, avatarProxy, pepper);
        string expectedKey = RateLimitPolicies.HashPartitionKey("avatar-proxy", "203.0.113.9", pepper);
        Assert.Equal(expectedKey, avatarProxyPartition.PartitionKey);
    }

    /// <summary>
    /// PHASE5_DESIGN.md S6d structural fix: `register-ip`'s permit count now reads
    /// `RateLimits:RegisterPerIpPerHour` (`Program.cs`), overridable so the browser test lane's one
    /// shared API process (well more than 5 registrations across its own classes) doesn't 429 —
    /// `PlaywrightFixture.cs` sets that key only in the environment of the API process it spawns.
    /// `_factory` here is exactly what every other test in this class already starts and never sets
    /// that key, so this is the guard that a missing override always yields the unchanged
    /// production default, never a silently widened one.
    /// </summary>
    [Fact]
    public void RegisterIpPermitLimit_DefaultsToFiveWithNoConfigurationOverride()
    {
        Assert.Equal(5, RateLimitPolicies.RegisterIpPermitLimit);
        Assert.Equal(
            5,
            RateLimitPolicies.Dimensions.Single(dimension => dimension.Name == "register-ip").PermitLimit);
    }

    [Fact]
    public async Task AnonymousExternalIntent_IsBoundedByIntentIp()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        for (int attempt = 0; attempt < 30; attempt++)
        {
            HttpResponseMessage accepted = await AntiforgeryTestClient.PostAsync(
                client,
                "/api/v1/auth/external/intent",
                JsonContent.Create(new
                {
                    provider = "google",
                    mode = "login",
                    returnTo = "/app",
                    ageGateAttested = true,
                }));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        HttpResponseMessage limited = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/external/intent",
            JsonContent.Create(new
            {
                provider = "google",
                mode = "login",
                returnTo = "/app",
                ageGateAttested = true,
            }));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task PartitionKey_UsesTheForwardedClientIpNotTheProxy()
    {
        await using var factory = ForwardedHeadersFactory();
        using HttpClient client = CreateClient(factory);

        for (int index = 0; index < 20; index++)
        {
            HttpResponseMessage allowed = await PostLoginProbeAsync(
                client,
                $"one-{index}@example.test",
                "192.0.2.10");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(client, "blocked@example.test", "192.0.2.10")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostLoginProbeAsync(client, "other@example.test", "192.0.2.11")).StatusCode);

        for (int index = 0; index < 20; index++)
        {
            HttpResponseMessage allowed = await PostLoginProbeAsync(
                client,
                $"spoof-{index}@example.test",
                $"203.0.113.{index + 1}, 192.0.2.12");
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(
                client,
                "spoof-blocked@example.test",
                "203.0.113.250, 192.0.2.12")).StatusCode);
    }

    [Fact]
    public async Task Login_ExhaustsBothTheIpAndTheEmailCeiling()
    {
        await using var factory = ForwardedHeadersFactory();
        using HttpClient client = CreateClient(factory);

        for (int index = 0; index < 10; index++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await PostLoginProbeAsync(
                    client,
                    "same@example.test",
                    "192.0.2.20")).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(
                client,
                "same@example.test",
                "192.0.2.20")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await PostLoginProbeAsync(
                client,
                "different@example.test",
                "192.0.2.20")).StatusCode);

        for (int index = 0; index < 20; index++)
        {
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await PostLoginProbeAsync(
                    client,
                    $"many-{index}@example.test",
                    "192.0.2.21")).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await PostLoginProbeAsync(
                client,
                "many-blocked@example.test",
                "192.0.2.21")).StatusCode);
    }

    [Fact]
    public async Task EveryChainMember_ExhaustsOnlyItsDocumentedPartition()
    {
        await using WebApplication application = await CreateLimiterProbeAsync();
        using HttpClient client = application.GetTestClient();
        LimiterCase[] cases =
        [
            Case("auth-ip", ApiOperationIds.ConsumeMagicLink),
            Case("login-email", ApiOperationIds.Login),
            Case("register-ip", ApiOperationIds.Register),
            Case("register-email", ApiOperationIds.Register),
            Case("auth-email", ApiOperationIds.ForgotPassword),
            Case("magic-link-ip", ApiOperationIds.RequestMagicLink),
            Case("deletion-request-email", ApiOperationIds.RequestAccountDeletion),
            Case("deletion-request-ip", ApiOperationIds.RequestAccountDeletion),
            Case("deletion-steam-ip", ApiOperationIds.StartSteamDeletion),
            Case("intent-ip", ApiOperationIds.IssueExternalIntent),
            Case("email-send", ApiOperationIds.ReauthenticateByEmail),
            Case("reauth", ApiOperationIds.IssueExternalIntent),
            Case("lookup", ApiOperationIds.CheckUsernameAvailability),
            Case("default", ApiOperationIds.GetSession),
        ];

        for (int caseIndex = 0; caseIndex < cases.Length; caseIndex++)
        {
            LimiterCase testCase = cases[caseIndex];
            for (int attempt = 0; attempt < testCase.Dimension.PermitLimit; attempt++)
            {
                using HttpResponseMessage allowed = await SendProbeAsync(
                    client,
                    testCase,
                    caseIndex,
                    attempt,
                    otherKey: false);
                Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
            }

            using HttpResponseMessage rejected = await SendProbeAsync(
                client,
                testCase,
                caseIndex,
                testCase.Dimension.PermitLimit,
                otherKey: false);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.True(rejected.Headers.RetryAfter?.Delta > TimeSpan.Zero);
            Assert.Equal(
                "application/problem+json",
                rejected.Content.Headers.ContentType?.MediaType);
            PartyUpProblem problem =
                (await rejected.Content.ReadFromJsonAsync<PartyUpProblem>())!;
            Assert.Equal("rate_limited", problem.Code);
            Assert.Equal(
                $"/probe/{testCase.OperationId}",
                problem.Instance);

            using HttpResponseMessage otherKey = await SendProbeAsync(
                client,
                testCase,
                caseIndex,
                0,
                otherKey: true);
            Assert.Equal(HttpStatusCode.NoContent, otherKey.StatusCode);

            if (!testCase.Dimension.CoversEveryApiOperation)
            {
                using HttpResponseMessage unrelated = await SendProbeAsync(
                    client,
                    testCase with { OperationId = ApiOperationIds.GetSession },
                    caseIndex,
                    0,
                    otherKey: false);
                Assert.Equal(HttpStatusCode.NoContent, unrelated.StatusCode);
            }
        }
    }

    [Fact]
    public async Task SteamImportMustSatisfyLookupImportAndDefaultDimensions_D049()
    {
        await using WebApplication application = await CreateLimiterProbeAsync();
        using HttpClient client = application.GetTestClient();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await SendOperationProbeAsync(
                    client,
                    ApiOperationIds.ImportSteamFriends,
                    "steam-user",
                    $"192.0.2.{attempt + 1}")).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await SendOperationProbeAsync(
                client,
                ApiOperationIds.ImportSteamFriends,
                "steam-user",
                "192.0.2.10")).StatusCode);

        for (int attempt = 0; attempt < 20; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await SendOperationProbeAsync(
                    client,
                    ApiOperationIds.GetUserProfile,
                    "lookup-user",
                    $"198.51.100.{attempt + 1}")).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await SendOperationProbeAsync(
                client,
                ApiOperationIds.ImportSteamFriends,
                "lookup-user",
                "198.51.100.50")).StatusCode);

        for (int attempt = 0; attempt < 300; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await SendOperationProbeAsync(
                    client,
                    ApiOperationIds.GetSession,
                    $"default-user-{attempt}",
                    "203.0.113.10")).StatusCode);
        }
        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await SendOperationProbeAsync(
                client,
                ApiOperationIds.ImportSteamFriends,
                "default-import-user",
                "203.0.113.10")).StatusCode);
    }

    [Theory]
    [InlineData("profile", 20)]
    [InlineData("friend-code", 20)]
    [InlineData("block", 20)]
    [InlineData("send", 10)]
    public async Task TargetStateCannotChangeRealRouteLeaseConsumption_D065(
        string routeKind,
        int permitLimit)
    {
        await using var factory = ForwardedHeadersFactory();
        using HttpClient client = CreateClient(factory);
        FriendEndpointUser target = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser hiddenActor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser missingActor = await FriendEndpointTest.SeedAsync(_factory);
        string targetCookie = await SignInAsync(client, target.Id);
        string hiddenCookie = await SignInAsync(client, hiddenActor.Id);
        string missingCookie = await SignInAsync(client, missingActor.Id);

        HttpResponseMessage block = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/blocks",
            JsonContent.Create(new { targetUsername = hiddenActor.Username }),
            targetCookie,
            forwardedFor: "192.0.2.30");
        Assert.Equal(HttpStatusCode.Created, block.StatusCode);

        string missingUsername = "miss" + target.Username[4..];
        string missingCode = target.FriendCode == "2222222222"
            ? "3333333333"
            : "2222222222";
        AntiforgeryPair? hiddenPair = routeKind is "block" or "send"
            ? await AntiforgeryTestClient.GetAsync(client, hiddenCookie)
            : null;
        AntiforgeryPair? missingPair = routeKind is "block" or "send"
            ? await AntiforgeryTestClient.GetAsync(client, missingCookie)
            : null;

        for (int attempt = 0; attempt < permitLimit; attempt++)
        {
            using HttpResponseMessage hidden = await SendTargetRouteAsync(
                client,
                routeKind,
                target.Username,
                target.FriendCode,
                hiddenCookie,
                hiddenPair,
                "192.0.2.31");
            using HttpResponseMessage missing = await SendTargetRouteAsync(
                client,
                routeKind,
                missingUsername,
                missingCode,
                missingCookie,
                missingPair,
                "192.0.2.32");
            await AssertTargetStateResponseEquivalentAsync(
                hidden,
                missing,
                HttpStatusCode.NotFound,
                "user_not_found");
        }

        using HttpResponseMessage hiddenRejected = await SendTargetRouteAsync(
            client,
            routeKind,
            target.Username,
            target.FriendCode,
            hiddenCookie,
            hiddenPair,
            "192.0.2.31");
        using HttpResponseMessage missingRejected = await SendTargetRouteAsync(
            client,
            routeKind,
            missingUsername,
            missingCode,
            missingCookie,
            missingPair,
            "192.0.2.32");
        await AssertTargetStateResponseEquivalentAsync(
            hiddenRejected,
            missingRejected,
            HttpStatusCode.TooManyRequests,
            "rate_limited");
        Assert.NotNull(hiddenRejected.Headers.RetryAfter?.Delta);
        Assert.Equal(
            hiddenRejected.Headers.RetryAfter?.Delta,
            missingRejected.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task StaleAndMissingIdRoutes_ConsumeSameRealDefaultLeases_D065()
    {
        await using var factory = ForwardedHeadersFactory();
        using HttpClient client = CreateClient(factory);
        FriendEndpointUser hiddenActor = await FriendEndpointTest.SeedAsync(_factory);
        FriendEndpointUser missingActor = await FriendEndpointTest.SeedAsync(_factory);
        string hiddenCookie = await SignInAsync(client, hiddenActor.Id);
        string missingCookie = await SignInAsync(client, missingActor.Id);
        RouteRequest[] hiddenRoutes = await CreateStaleActionRoutesAsync(
            client,
            hiddenActor,
            hiddenCookie);
        RouteRequest[] missingRoutes =
        [
            new(HttpMethod.Post, $"/api/v1/friend-requests/{Guid.NewGuid():D}/accept"),
            new(HttpMethod.Post, $"/api/v1/friend-requests/{Guid.NewGuid():D}/reject"),
            new(HttpMethod.Delete, $"/api/v1/friend-requests/{Guid.NewGuid():D}"),
            new(HttpMethod.Delete, $"/api/v1/friends/{Guid.NewGuid():D}"),
        ];
        AntiforgeryPair hiddenPair = await AntiforgeryTestClient.GetAsync(client, hiddenCookie);
        AntiforgeryPair missingPair = await AntiforgeryTestClient.GetAsync(client, missingCookie);

        for (int attempt = 0; attempt < 300; attempt++)
        {
            RouteRequest hiddenRoute = hiddenRoutes[attempt % hiddenRoutes.Length];
            RouteRequest missingRoute = missingRoutes[attempt % missingRoutes.Length];
            using HttpResponseMessage hidden = await SendUnsafeWithPairAsync(
                client,
                hiddenRoute,
                hiddenCookie,
                hiddenPair,
                "192.0.2.41");
            using HttpResponseMessage missing = await SendUnsafeWithPairAsync(
                client,
                missingRoute,
                missingCookie,
                missingPair,
                "192.0.2.42");
            string code = attempt % hiddenRoutes.Length == 3
                ? "friendship_not_found"
                : "friend_request_not_found";
            await AssertTargetStateResponseEquivalentAsync(
                hidden,
                missing,
                HttpStatusCode.NotFound,
                code);
        }

        using HttpResponseMessage hiddenRejected = await SendUnsafeWithPairAsync(
            client,
            hiddenRoutes[0],
            hiddenCookie,
            hiddenPair,
            "192.0.2.41");
        using HttpResponseMessage missingRejected = await SendUnsafeWithPairAsync(
            client,
            missingRoutes[0],
            missingCookie,
            missingPair,
            "192.0.2.42");
        await AssertTargetStateResponseEquivalentAsync(
            hiddenRejected,
            missingRejected,
            HttpStatusCode.TooManyRequests,
            "rate_limited");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BlockedAndAbsentSteamImports_ConsumeEveryRealRouteLease_D065(
        bool blocked)
    {
        var steam = new RoutingSteamFriendListClient();
        await using WebApplicationFactory<Program> factory = SteamRateLimitFactory(steam);
        using HttpClient client = CreateClient(factory);
        RateLimitDimension importDimension = Assert.Single(
            RateLimitPolicies.Dimensions,
            dimension => dimension.Name == "steam-friend-import");
        RateLimitDimension lookupDimension = Assert.Single(
            RateLimitPolicies.Dimensions,
            dimension => dimension.Name == "friend-lookup");
        RateLimitDimension defaultDimension = Assert.Single(
            RateLimitPolicies.Dimensions,
            dimension => dimension.Name == "default");
        Assert.True(importDimension.PermitLimit < lookupDimension.PermitLimit);
        Assert.True(importDimension.PermitLimit < defaultDimension.PermitLimit);

        FriendEndpointUser importActor = await SeedSteamLimiterActorAsync(
            factory,
            steam,
            blocked,
            "import");
        string importCookie = await SignInAsync(client, importActor.Id);
        AntiforgeryPair importPair = await AntiforgeryTestClient.GetAsync(client, importCookie);
        for (int attempt = 0; attempt < importDimension.PermitLimit; attempt++)
        {
            using HttpResponseMessage allowed = await SendSteamImportAsync(
                client,
                importCookie,
                importPair,
                "192.0.2.61");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        using (HttpResponseMessage rejected = await SendSteamImportAsync(
            client,
            importCookie,
            importPair,
            "192.0.2.61"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        FriendEndpointUser lookupActor = await SeedSteamLimiterActorAsync(
            factory,
            steam,
            blocked,
            "lookup");
        string lookupCookie = await SignInAsync(client, lookupActor.Id);
        AntiforgeryPair lookupPair = await AntiforgeryTestClient.GetAsync(client, lookupCookie);
        for (int attempt = 0;
             attempt < lookupDimension.PermitLimit - importDimension.PermitLimit;
             attempt++)
        {
            using HttpResponseMessage allowed = await SendGetAsync(
                client,
                $"/api/v1/users/by-username/{lookupActor.Username}",
                lookupCookie,
                "192.0.2.62");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        for (int attempt = 0; attempt < importDimension.PermitLimit; attempt++)
        {
            using HttpResponseMessage allowed = await SendSteamImportAsync(
                client,
                lookupCookie,
                lookupPair,
                "192.0.2.62");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        using (HttpResponseMessage rejected = await SendGetAsync(
            client,
            $"/api/v1/users/by-username/{lookupActor.Username}",
            lookupCookie,
            "192.0.2.62"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        }

        FriendEndpointUser defaultActor = await SeedSteamLimiterActorAsync(
            factory,
            steam,
            blocked,
            "default");
        string defaultCookie = await SignInAsync(client, defaultActor.Id);
        AntiforgeryPair defaultPair = await AntiforgeryTestClient.GetAsync(client, defaultCookie);
        for (int attempt = 0;
             attempt < defaultDimension.PermitLimit - importDimension.PermitLimit;
             attempt++)
        {
            using HttpResponseMessage allowed = await SendForwardedSessionAsync(
                client,
                "192.0.2.63");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        for (int attempt = 0; attempt < importDimension.PermitLimit; attempt++)
        {
            using HttpResponseMessage allowed = await SendSteamImportAsync(
                client,
                defaultCookie,
                defaultPair,
                "192.0.2.63");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        using HttpResponseMessage defaultRejected = await SendForwardedSessionAsync(
            client,
            "192.0.2.63");
        Assert.Equal(HttpStatusCode.TooManyRequests, defaultRejected.StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
        ForwardedHeadersFactory() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Network:TrustedProxies:0"] = "10.0.0.2",
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(
                    IPAddress.Parse("10.0.0.2"))));
        });

    private WebApplicationFactory<Program> SteamRateLimitFactory(
        RoutingSteamFriendListClient steam) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Network:TrustedProxies:0"] = "10.0.0.2",
                }));
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(
                    IPAddress.Parse("10.0.0.2"))));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISteamFriendListClient>();
                services.AddSingleton<ISteamFriendListClient>(steam);
            });
        });

    private async Task<FriendEndpointUser> SeedSteamLimiterActorAsync(
        WebApplicationFactory<Program> factory,
        RoutingSteamFriendListClient steam,
        bool blocked,
        string discriminator)
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string importerSteamId = $"i{discriminator[0]}{suffix}";
        string candidateSteamId = $"c{discriminator[0]}{suffix}";
        FriendEndpointUser actor = await SteamImportIntegrationData.SeedLinkedAsync(
            _factory,
            importerSteamId);
        steam.Results[importerSteamId] = new SteamFriendListSuccess(
            [new SteamFriend(candidateSteamId, "Equivalent candidate", null)]);
        if (!blocked)
        {
            return actor;
        }

        FriendEndpointUser candidate = await SteamImportIntegrationData.SeedLinkedAsync(
            _factory,
            candidateSteamId);
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        BlockGraphCommandResult result = await scope.ServiceProvider
            .GetRequiredService<BlockUserUseCase>()
            .ExecuteAsync(actor.Id, candidate.Username, default);
        Assert.True(result.Succeeded);
        return actor;
    }

    private static Task<HttpResponseMessage> SendSteamImportAsync(
        HttpClient client,
        string authCookie,
        AntiforgeryPair antiforgery,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/friends/imports/steam");
        request.Headers.Add("X-CSRF-TOKEN", antiforgery.Token);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{authCookie}; {antiforgery.Cookie}");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendForwardedSessionAsync(
        HttpClient client,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private async Task<RouteRequest[]> CreateStaleActionRoutesAsync(
        HttpClient client,
        FriendEndpointUser actor,
        string actorCookie)
    {
        var routes = new List<RouteRequest>();
        foreach (string action in new[] { "accept", "reject", "cancel", "unfriend" })
        {
            FriendEndpointUser counterpart = await FriendEndpointTest.SeedAsync(_factory);
            string counterpartCookie = await SignInAsync(client, counterpart.Id);
            bool incoming = action is "accept" or "reject";
            string requesterCookie = incoming ? counterpartCookie : actorCookie;
            string targetUsername = incoming ? actor.Username : counterpart.Username;
            HttpResponseMessage pending = await AntiforgeryTestClient.PostAsync(
                client,
                "/api/v1/friend-requests",
                JsonContent.Create(new { targetUsername }),
                requesterCookie,
                forwardedFor: "192.0.2.40");
            pending.EnsureSuccessStatusCode();
            Guid friendshipId = (await pending.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("friendshipId")
                .GetGuid();

            if (action == "unfriend")
            {
                HttpResponseMessage accepted = await AntiforgeryTestClient.PostAsync(
                    client,
                    $"/api/v1/friend-requests/{friendshipId:D}/accept",
                    content: null,
                    counterpartCookie,
                    forwardedFor: "192.0.2.40");
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            }

            HttpResponseMessage blocked = await AntiforgeryTestClient.PostAsync(
                client,
                "/api/v1/blocks",
                JsonContent.Create(new { targetUsername = counterpart.Username }),
                actorCookie,
                forwardedFor: "192.0.2.40");
            Assert.Equal(HttpStatusCode.Created, blocked.StatusCode);
            routes.Add(action switch
            {
                "accept" => new(
                    HttpMethod.Post,
                    $"/api/v1/friend-requests/{friendshipId:D}/accept"),
                "reject" => new(
                    HttpMethod.Post,
                    $"/api/v1/friend-requests/{friendshipId:D}/reject"),
                "cancel" => new(
                    HttpMethod.Delete,
                    $"/api/v1/friend-requests/{friendshipId:D}"),
                "unfriend" => new(
                    HttpMethod.Delete,
                    $"/api/v1/friends/{friendshipId:D}"),
                _ => throw new InvalidOperationException($"Unknown action '{action}'."),
            });
        }

        return routes.ToArray();
    }

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return Slice5HttpTest.AuthCookie(response);
    }

    private static Task<HttpResponseMessage> SendTargetRouteAsync(
        HttpClient client,
        string routeKind,
        string username,
        string friendCode,
        string authCookie,
        AntiforgeryPair? antiforgery,
        string forwardedFor) =>
        routeKind switch
        {
            "profile" => SendGetAsync(
                client,
                $"/api/v1/users/by-username/{username}",
                authCookie,
                forwardedFor),
            "friend-code" => SendGetAsync(
                client,
                $"/api/v1/friend-codes/{friendCode}",
                authCookie,
                forwardedFor),
            "block" => SendUnsafeWithPairAsync(
                client,
                new RouteRequest(HttpMethod.Post, "/api/v1/blocks", username),
                authCookie,
                antiforgery!,
                forwardedFor),
            "send" => SendUnsafeWithPairAsync(
                client,
                new RouteRequest(HttpMethod.Post, "/api/v1/friend-requests", username),
                authCookie,
                antiforgery!,
                forwardedFor),
            _ => throw new ArgumentOutOfRangeException(nameof(routeKind)),
        };

    private static Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string path,
        string authCookie,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(HeaderNames.Cookie, authCookie);
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendUnsafeWithPairAsync(
        HttpClient client,
        RouteRequest route,
        string authCookie,
        AntiforgeryPair antiforgery,
        string forwardedFor)
    {
        var request = new HttpRequestMessage(route.Method, route.Path)
        {
            Content = route.TargetUsername is null
                ? null
                : JsonContent.Create(new { targetUsername = route.TargetUsername }),
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgery.Token);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{authCookie}; {antiforgery.Cookie}");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private static async Task AssertTargetStateResponseEquivalentAsync(
        HttpResponseMessage hidden,
        HttpResponseMessage missing,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, hidden.StatusCode);
        Assert.Equal(hidden.StatusCode, missing.StatusCode);
        Assert.Equal(
            HeaderSnapshot(hidden),
            HeaderSnapshot(missing));
        Assert.Equal(
            await NormalizedProblemAsync(hidden, code),
            await NormalizedProblemAsync(missing, code));
    }

    private static async Task<string> NormalizedProblemAsync(
        HttpResponseMessage response,
        string code)
    {
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.Equal(code, root.GetProperty("code").GetString());
        return JsonSerializer.Serialize(new
        {
            type = root.GetProperty("type").GetString(),
            title = root.GetProperty("title").GetString(),
            status = root.GetProperty("status").GetInt32(),
            code = root.GetProperty("code").GetString(),
        });
    }

    private static Dictionary<string, string> HeaderSnapshot(
        HttpResponseMessage response) =>
        response.Headers
            .Concat(response.Content.Headers)
            .Where(header => !header.Key.Equals("Date", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateClient(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static Task<HttpResponseMessage> PostLoginProbeAsync(
        HttpClient client,
        string email,
        string forwardedFor) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email, password = "not-the-password" }),
            forwardedFor: forwardedFor);

    private static LimiterCase Case(string dimensionName, string operationId) =>
        new(
            Assert.Single(
                RateLimitPolicies.Dimensions,
                dimension => dimension.Name == dimensionName),
            operationId);

    private static async Task<WebApplication> CreateLimiterProbeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = RateLimitPolicies.CreateGlobalLimiter(
                Encoding.UTF8.GetBytes("behavioral-rate-limit-test-pepper"));
            options.OnRejected = RateLimitPolicies.OnRejectedAsync;
        });
        WebApplication application = builder.Build();
        application.UseRouting();
        application.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Probe-IP", out var ip))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            }

            if (context.Request.Headers.TryGetValue("X-Probe-User", out var user))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, user.ToString())],
                    IdentityConstants.ApplicationScheme));
            }

            await next(context);
        });
        application.UseRateLimitEmailPartitioning();
        application.UseRateLimiter();
        string[] operationIds = RateLimitPolicies.Dimensions
            .SelectMany(dimension => dimension.OperationIds)
            .Append(ApiOperationIds.GetSession)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string operationId in operationIds)
        {
            application.MapPost(
                    $"/probe/{operationId}",
                    () => Results.NoContent())
                .WithName(operationId);
        }

        await application.StartAsync();
        return application;
    }

    private static Task<HttpResponseMessage> SendProbeAsync(
        HttpClient client,
        LimiterCase testCase,
        int caseIndex,
        int attempt,
        bool otherKey)
    {
        string keySuffix = otherKey ? "other" : "primary";
        string ip = testCase.Dimension.PartitionKind == RateLimitPartitionKind.Ip
            ? $"192.0.2.{caseIndex + (otherKey ? 101 : 1)}"
            : $"198.51.{caseIndex + 1}.{attempt + (otherKey ? 101 : 1)}";
        string email = testCase.Dimension.PartitionKind == RateLimitPartitionKind.Email
            ? $"{keySuffix}-{caseIndex}@example.test"
            : $"attempt-{caseIndex}-{attempt}-{keySuffix}@example.test";
        string user = testCase.Dimension.PartitionKind == RateLimitPartitionKind.User
            ? $"{keySuffix}-user-{caseIndex}"
            : $"attempt-user-{caseIndex}-{attempt}-{keySuffix}";
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/probe/{testCase.OperationId}")
        {
            Content = JsonContent.Create(new { email }),
        };
        request.Headers.Add("X-Probe-IP", ip);
        if (testCase.Dimension.PartitionKind == RateLimitPartitionKind.User ||
            testCase.Dimension.Name == "email-send")
        {
            request.Headers.Add("X-Probe-User", user);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendOperationProbeAsync(
        HttpClient client,
        string operationId,
        string user,
        string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/probe/{operationId}");
        request.Headers.Add("X-Probe-IP", ip);
        request.Headers.Add("X-Probe-User", user);
        return client.SendAsync(request);
    }

    private sealed record LimiterCase(
        RateLimitDimension Dimension,
        string OperationId);

    private sealed record RouteRequest(
        HttpMethod Method,
        string Path,
        string? TargetUsername = null);

    private sealed class RemoteIpStartupFilter(IPAddress proxyAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, following) =>
                {
                    context.Connection.RemoteIpAddress = proxyAddress;
                    await following(context);
                });
                next(application);
            };
    }
}
