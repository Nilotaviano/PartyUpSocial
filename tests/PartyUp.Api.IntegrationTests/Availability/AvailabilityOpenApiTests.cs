using System.Text.Json;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md P5-HTTP-01/A-071: operation ids stay globally unique with the Phase 5 surface
/// added, the new-operation set is closed (round-1 fix F9 — the prior version only asserted
/// <c>Contains</c> on seven ids with no schema check), and every new problem code's documented status
/// is present on the routes §8 assigns it to.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityOpenApiTests : IAsyncLifetime, IDisposable
{
    /// <summary>§8's twelve new operations — the closed set this slice may add.</summary>
    private static readonly string[] NewPhase5OperationIds =
    [
        ApiOperationIds.GetMyAvailability,
        ApiOperationIds.CreateAvailabilityBlock,
        ApiOperationIds.EditAvailabilityBlock,
        ApiOperationIds.DeleteAvailabilityBlock,
        ApiOperationIds.CreateAvailabilitySeries,
        ApiOperationIds.DeleteAvailabilitySeries,
        ApiOperationIds.EditAvailabilityOccurrence,
        ApiOperationIds.DeleteAvailabilityOccurrence,
        ApiOperationIds.FindOverlapWindows,
        ApiOperationIds.GetHomeSuggestions,
        ApiOperationIds.GetCalendarView,
        ApiOperationIds.GetActionableInbox,
    ];

    /// <summary>
    /// The fifteen operation ids the Phase 6 poll HTTP surface added, kept as its own list separate
    /// from <see cref="PreExistingOperationIds"/> so the two slices' provenance stays legible.
    /// </summary>
    private static readonly string[] NewPollOperationIds =
    [
        ApiOperationIds.CreatePoll, ApiOperationIds.GetPoll, ApiOperationIds.SuggestPollOption,
        ApiOperationIds.DeletePollOption, ApiOperationIds.VoteOnPollOption, ApiOperationIds.FinalizePoll,
        ApiOperationIds.CancelPoll, ApiOperationIds.CreatePollGuestLinks, ApiOperationIds.RevokePollGuestLink,
        ApiOperationIds.RemovePollGuest, ApiOperationIds.RequestPollJoin, ApiOperationIds.WithdrawPollJoinRequest,
        ApiOperationIds.ListPollJoinRequests, ApiOperationIds.ApprovePollJoinRequest,
        ApiOperationIds.RejectPollJoinRequest,
    ];

    /// <summary>
    /// S8 hardening (closes the round-1/round-2 "swap-hole": a bare count anchor passes even when one
    /// operation is silently swapped for another, so nothing short of the full set closes it).
    /// Every non-Phase-5 operation id in the shipped document uses a raw <c>.WithName("…")</c> string
    /// literal rather than an <c>ApiOperationIds</c> constant (the pre-Phase-4 surface predates that
    /// convention), so this is a hand-authored closed list, not a re-derivation from a constants class
    /// that does not enumerate them. Captured against this test factory's own <c>Testing</c>-environment
    /// host (which additionally maps the four <c>/api/v1/_slice2/*</c> sign-in helper routes
    /// <c>Program.cs</c> only registers under that environment name): the 89 operation ids the Phase 5
    /// surface was built on top of, plus the 12 new ones in <see cref="NewPhase5OperationIds"/> and the
    /// 15 poll ones in <see cref="NewPollOperationIds"/> — 116 total. A deliberate future change to the
    /// shipped surface must update this list explicitly; it is not meant to silently absorb an added,
    /// removed, or renamed operation.
    /// </summary>
    private static readonly string[] PreExistingOperationIds =
    [
        "AcceptFriendRequest", "ApproveSessionJoinRequest", "BanAdminUser", "BlockUser",
        "CancelFriendRequest", "CancelSession",
        "ChangeEmail", "ChangeMyAvatar", "ChangeMyDisplayName", "ChangeMyLocale",
        "ChangeMyReminderDefault", "ChangeMyTimeZone", "ChangeMyUsername", "ChangePassword",
        "ChangePlatformHandle", "CheckUsernameAvailability", "ClaimGuestSessionLink",
        "CompleteOnboarding", "CompleteSteamDeletion", "CompleteSteamExternal",
        "ConfirmAccountDeletion", "ConsumeMagicLink", "CreateSession", "CreateSessionGuestLinks",
        "DeleteAuthMethod", "DeleteMe", "DeletePlatformLink", "EditSession", "ForgotPassword",
        "GetAdminUser", "GetAntiforgery", "GetAuthMethods", "GetFriendShare", "GetGuestSession",
        "GetMyProfile", "GetOnboardingState", "GetPlatformLinks", "GetSession", "GetSessionDetail",
        "GetUserProfile", "ImportSteamFriends", "InviteSessionUsers", "IssueExternalIntent",
        "LeaveSession", "LinkPlatform", "ListAdminUsers", "ListBlocks", "ListFriendRequests",
        "ListFriends", "ListSessionJoinRequests", "ListSessions", "Login", "Logout",
        "PromoteSessionWaitlistParticipant", "ReadDeletionJournal", "Reauthenticate",
        "ReauthenticateByEmail", "Register", "RejectFriendRequest", "RejectSessionJoinRequest",
        "RejectSessionWaitlistParticipant", "RemoveSessionGuest", "RenameGuestSessionIdentity",
        "RequestAccountDeletion", "RequestEmailVerification", "RequestMagicLink",
        "RequestSessionJoin", "ResetPassword", "ResolveFriendCode", "RespondAsGuest",
        "RespondToSessionInvitation", "RevokeSessionGuestLink", "RunDeletionSweep",
        "SendFriendRequest", "SetPassword", "SetSessionReminder", "Slice2Authenticated",
        "Slice2Onboarded", "Slice2SignIn", "Slice2SignOut", "StartSteamDeletion",
        "StartSteamExternal", "UnbanAdminUser", "UnblockUser", "Unfriend",
        "ValidateOnboardingProfile", "VerifyEmail", "VerifyEmailChange", "WithdrawSessionJoinRequest",
    ];

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task OperationIdsAreGloballyUniqueAndEveryProblemCodeMaps_A071()
    {
        using JsonDocument document = await FetchDocumentAsync();
        JsonElement paths = document.RootElement.GetProperty("paths");

        List<string> operationIds = [];
        foreach (JsonProperty path in paths.EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (operation.Value.TryGetProperty("operationId", out JsonElement operationId))
                {
                    operationIds.Add(operationId.GetString()!);
                }
            }
        }

        Assert.Equal(operationIds.Count, operationIds.Distinct(StringComparer.Ordinal).Count());

        // F9: the closed set — every one of the 12 new operations appears exactly once, A-064's
        // transport exception is absent (it sits outside /api/v1 and outside this document by
        // design), and the document's total operation count is exactly the captured anchor — an
        // operation silently added or removed anywhere in the document fails this, not just a
        // `Contains` check on the Phase 5 subset.
        foreach (string newId in NewPhase5OperationIds)
        {
            Assert.Single(operationIds, id => id == newId);
        }

        Assert.DoesNotContain(ApiOperationIds.ExchangeGuestLink, operationIds);

        // S8 hardening: the exact shipped set, not merely its count — a swap (one operation added,
        // a different one silently removed) leaves the count anchor green while this catches it.
        string[] expectedOperationIds = [.. PreExistingOperationIds, .. NewPhase5OperationIds, .. NewPollOperationIds];
        Assert.Equal(
            expectedOperationIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            operationIds.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        // For the new operations: request/response schemas are non-empty, real named components —
        // never `object`/`unknown` placeholders. Specifically, the F1 discriminated union.
        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        string[] expectedSchemas =
        [
            "AvailabilityBlockView",
            "AvailabilitySeriesView",
            "MaskedParticipant",
            "MyAvailabilityLayerResponse",
            "OverlapSearchResponse",
            "HomeSuggestionsResponse",
            "CalendarViewResponse",
            "ActionableInboxResponse",
            "CreateAvailabilityBlockRequest",
            "CreateAvailabilitySeriesRequest",
            "EditOccurrenceRequest",
            "EditOccurrenceResponse",
            "EditOccurrenceResponseOnlyThis",
            "EditOccurrenceResponseThisAndFuture",
        ];
        foreach (string schemaName in expectedSchemas)
        {
            Assert.True(schemas.TryGetProperty(schemaName, out JsonElement schema), $"Missing schema {schemaName}");
            Assert.True(
                schema.EnumerateObject().Any(),
                $"Schema {schemaName} must not be an empty/untyped placeholder.");
        }

        // The union itself: a discriminator on "scope" mapping to the two named derived schemas —
        // not `Produces<object>()`'s prior untyped shape.
        JsonElement editOccurrenceResponse = schemas.GetProperty("EditOccurrenceResponse");
        Assert.True(editOccurrenceResponse.TryGetProperty("discriminator", out JsonElement discriminator));
        Assert.Equal("scope", discriminator.GetProperty("propertyName").GetString());
        JsonElement mapping = discriminator.GetProperty("mapping");
        Assert.True(mapping.TryGetProperty("onlyThis", out _));
        Assert.True(mapping.TryGetProperty("thisAndFuture", out _));

        JsonElement onlyThisSchema = schemas.GetProperty("EditOccurrenceResponseOnlyThis");
        Assert.True(onlyThisSchema.GetProperty("properties").TryGetProperty("block", out _));
        JsonElement thisAndFutureSchema = schemas.GetProperty("EditOccurrenceResponseThisAndFuture");
        Assert.True(thisAndFutureSchema.GetProperty("properties").TryGetProperty("original", out _));
        Assert.True(thisAndFutureSchema.GetProperty("properties").TryGetProperty("successor", out _));

        // §8's closed problem-code inventory: five new codes, each with its documented status present
        // on the operations that can emit it.
        AssertStatusDocumented(paths, "/api/v1/me/availability", "get", 400); // invalid_range / range_too_large
        AssertStatusDocumented(paths, "/api/v1/me/availability/blocks", "post", 400); // availability_validation
        AssertStatusDocumented(paths, "/api/v1/me/availability/blocks/{blockId}", "patch", 404); // availability_not_found
        AssertStatusDocumented(paths, "/api/v1/me/availability/blocks/{blockId}", "patch", 400);
        AssertStatusDocumented(
            paths, "/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}", "patch", 404); // occurrence_not_found
        AssertStatusDocumented(
            paths, "/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}", "patch", 400);
        AssertStatusDocumented(paths, "/api/v1/availability/overlaps", "get", 400); // invalid_range / range_too_large
        AssertStatusDocumented(paths, "/api/v1/calendar", "get", 400);

        // F8: the idempotent-204 delete routes no longer document an unreachable 404.
        AssertStatusNotDocumented(paths, "/api/v1/me/availability/blocks/{blockId}", "delete", 404);
        AssertStatusNotDocumented(paths, "/api/v1/me/availability/series/{seriesId}", "delete", 404);
    }

    private static void AssertStatusDocumented(JsonElement paths, string path, string method, int status)
    {
        JsonElement responses = paths.GetProperty(path).GetProperty(method).GetProperty("responses");
        Assert.True(
            responses.TryGetProperty(status.ToString(System.Globalization.CultureInfo.InvariantCulture), out _),
            $"{method.ToUpperInvariant()} {path} must document status {status}.");
    }

    private static void AssertStatusNotDocumented(JsonElement paths, string path, string method, int status)
    {
        JsonElement responses = paths.GetProperty(path).GetProperty(method).GetProperty("responses");
        Assert.False(
            responses.TryGetProperty(status.ToString(System.Globalization.CultureInfo.InvariantCulture), out _),
            $"{method.ToUpperInvariant()} {path} must not document unreachable status {status}.");
    }

    private async Task<JsonDocument> FetchDocumentAsync()
    {
        using HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
