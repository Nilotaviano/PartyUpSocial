using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.ErrorHandling;

// Exposes RateLimitPolicies' otherwise-private partitioning internals to
// GuestRateLimitTests.MissingOrMalformedLinkRouteValueFallsBackToTheIpPartition_A073 — the shipped
// guest routes always carry a route-constrained guid, so that fallback branch is only reachable by
// calling the partition function directly rather than through an HTTP request.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PartyUp.Api.IntegrationTests")]

namespace PartyUp.Api.Security;

public enum RateLimitPartitionKind
{
    Ip,
    Email,
    User,

    /// <summary>
    /// A-073's <c>guest-write</c> dimension: keyed on the <c>{guestLinkId}</c> route value (an
    /// unauthenticated caller has no validated credential at partition time, since this runs before
    /// <c>UseAuthorization</c> — see <see cref="RateLimitPolicies.PartitionFor"/>), falling back to
    /// the caller's IP when that route value is absent or unparseable.
    /// </summary>
    GuestLink,
}

public sealed record RateLimitDimension(
    string Name,
    int PermitLimit,
    TimeSpan Window,
    RateLimitPartitionKind PartitionKind,
    IReadOnlySet<string> OperationIds,
    bool CoversEveryApiOperation = false,
    bool ExcludesDefault = false);

public sealed class RateLimitPartitionPepper
{
    private static readonly Action<ILogger, Exception?> LogMissingPepper =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(LogMissingPepper)),
            "Security:PartitionPepper is empty; rate-limit partitions will reset on restart");

    public RateLimitPartitionPepper(
        IConfiguration configuration,
        ILogger<RateLimitPartitionPepper> logger)
    {
        string? configured = configuration["Security:PartitionPepper"];
        if (string.IsNullOrEmpty(configured))
        {
            Value = RandomNumberGenerator.GetBytes(32);
            LogMissingPepper(logger, null);
            return;
        }

        Value = Encoding.UTF8.GetBytes(configured);
    }

    public byte[] Value { get; }
}

public static class RateLimitPolicies
{
    private const string EmailPartitionItem = "PartyUp.RateLimit.NormalizedEmail";

    private static int _registerIpPermitLimit = 5;
    private static IReadOnlyList<RateLimitDimension>? _cachedDimensions;
    private static IReadOnlySet<string>? _cachedClaimedOperationIds;

    /// <summary>
    /// register-ip's permit count (5/hour/IP in production, unconditionally). Overridable via
    /// <c>RateLimits:RegisterPerIpPerHour</c> — <c>Program.cs</c> reads that key once at startup and
    /// assigns it here before <see cref="CreateGlobalLimiter"/> is ever called; with no configuration
    /// key set (every production deployment) this stays exactly 5, byte-identical to before. The
    /// only caller that sets it away from 5 is <c>PlaywrightFixture.cs</c>, in the environment of the
    /// API process it spawns for the browser test lane — never in-process, never a second dimension.
    /// A settable static (not a constructor parameter) because <see cref="Dimensions"/> is a plain
    /// static list every existing caller (this class, its tests) already reads with no DI container
    /// in between. The setter invalidates <see cref="_cachedDimensions"/>: <see cref="Dimensions"/>
    /// is read inside per-request rate-limiting middleware, so it must stay a build-once list,
    /// rebuilt only on an actual change here, not 26 records and 26 <see cref="HashSet{T}"/>s
    /// allocated on every request.
    /// </summary>
    public static int RegisterIpPermitLimit
    {
        get => _registerIpPermitLimit;
        set
        {
            _registerIpPermitLimit = value;
            _cachedDimensions = null;
            _cachedClaimedOperationIds = null;
        }
    }

    public static IReadOnlyList<RateLimitDimension> Dimensions => _cachedDimensions ??= BuildDimensions();

    /// <summary>
    /// Operation ids of dimensions that declare <see cref="RateLimitDimension.ExcludesDefault"/> —
    /// <c>default</c> skips only these in <see cref="PartitionFor"/>, so a wider ceiling like
    /// <c>avatar-proxy</c> (600/min) is not made inert by <c>default</c>'s 300/min. Every other
    /// explicit dimension deliberately stacks on top of <c>default</c> (the D-049 Steam import must
    /// satisfy lookup, import and default simultaneously).
    /// </summary>
    private static IReadOnlySet<string> ClaimedOperationIds => _cachedClaimedOperationIds ??=
        new HashSet<string>(
            Dimensions
                .Where(dimension => dimension.ExcludesDefault)
                .SelectMany(dimension => dimension.OperationIds),
            StringComparer.Ordinal);

    private static IReadOnlyList<RateLimitDimension> BuildDimensions() =>
    [
        new(
            "auth-ip",
            20,
            TimeSpan.FromMinutes(15),
            RateLimitPartitionKind.Ip,
            Operations(
                ApiOperationIds.Login,
                ApiOperationIds.ConsumeMagicLink,
                ApiOperationIds.ResetPassword,
                ApiOperationIds.VerifyEmail)),
        new(
            "login-email",
            10,
            TimeSpan.FromMinutes(15),
            RateLimitPartitionKind.Email,
            Operations(ApiOperationIds.Login)),
        new(
            "register-ip",
            RegisterIpPermitLimit,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.Register)),
        new(
            "register-email",
            3,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Email,
            Operations(ApiOperationIds.Register)),
        new(
            "auth-email",
            5,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Email,
            Operations(ApiOperationIds.RequestMagicLink, ApiOperationIds.ForgotPassword)),
        new(
            "magic-link-ip",
            20,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.RequestMagicLink)),
        new(
            "deletion-request-email",
            3,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Email,
            Operations(ApiOperationIds.RequestAccountDeletion)),
        new(
            "deletion-request-ip",
            10,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.RequestAccountDeletion)),
        new(
            "deletion-steam-ip",
            10,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.StartSteamDeletion, ApiOperationIds.CompleteSteamDeletion)),
        new(
            "intent-ip",
            30,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.IssueExternalIntent)),
        new(
            "email-send",
            3,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.RequestEmailVerification, ApiOperationIds.ReauthenticateByEmail)),
        new(
            "reauth",
            5,
            TimeSpan.FromMinutes(15),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.Reauthenticate,
                ApiOperationIds.ReauthenticateByEmail,
                ApiOperationIds.ChangePassword,
                ApiOperationIds.SetPassword,
                ApiOperationIds.IssueExternalIntent,
                ApiOperationIds.DeleteAuthMethod,
                ApiOperationIds.DeletePlatformLink,
                ApiOperationIds.ChangeEmail,
                ApiOperationIds.DeleteMe)),
        new(
            "lookup",
            30,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.CheckUsernameAvailability)),
        new(
            "friend-lookup",
            20,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.GetUserProfile,
                ApiOperationIds.ResolveFriendCode,
                ApiOperationIds.BlockUser,
                ApiOperationIds.ImportSteamFriends)),
        new(
            "friend-request-send",
            10,
            TimeSpan.FromHours(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.SendFriendRequest)),
        new(
            "steam-friend-import",
            5,
            TimeSpan.FromMinutes(15),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.ImportSteamFriends)),
        new(
            "session-write",
            30,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.CreateSession,
                ApiOperationIds.EditSession,
                ApiOperationIds.CancelSession,
                ApiOperationIds.InviteSessionUsers,
                ApiOperationIds.CreateSessionGuestLinks,
                ApiOperationIds.RevokeSessionGuestLink,
                ApiOperationIds.RemoveSessionGuest,
                ApiOperationIds.RespondToSessionInvitation,
                ApiOperationIds.LeaveSession,
                ApiOperationIds.RequestSessionJoin,
                ApiOperationIds.WithdrawSessionJoinRequest,
                ApiOperationIds.SetSessionReminder)),
        new(
            "session-attach",
            60,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.ApproveSessionJoinRequest,
                ApiOperationIds.RejectSessionJoinRequest,
                ApiOperationIds.PromoteSessionWaitlistParticipant,
                ApiOperationIds.RejectSessionWaitlistParticipant)),
        new(
            "guest-link-exchange",
            20,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.ExchangeGuestLink)),
        // AVATARS_DESIGN.md §3.3: sized for a page load, not an API call -- a 150-friend roster
        // fans out to ~150 GETs in one paint, which would burn through `default`'s 300/min/IP on
        // its own and 429 the caller's real API traffic behind it.
        new(
            "avatar-proxy",
            600,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.GetAvatarProxy),
            ExcludesDefault: true),
        new(
            "guest-write",
            20,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.GuestLink,
            Operations(
                ApiOperationIds.ClaimGuestSessionLink,
                ApiOperationIds.RenameGuestSessionIdentity,
                ApiOperationIds.RespondAsGuest,
                // PHASE6_DESIGN.md §4.2 (ticket-P6-S4b): poll guest ops join this existing dimension
                // rather than a new one — GetGuestPollView is unassigned (default dimension), a GET.
                ApiOperationIds.ClaimGuestPollLink,
                ApiOperationIds.VoteOnPollAsGuest,
                ApiOperationIds.SuggestPollOptionAsGuest,
                ApiOperationIds.RenameGuestPollIdentity)),
        // PHASE5_DESIGN.md A-079: five new User-partitioned dimensions, assigned exhaustively so §8
        // has no unassigned route.
        new(
            "availability-write",
            60,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.CreateAvailabilityBlock,
                ApiOperationIds.EditAvailabilityBlock,
                ApiOperationIds.DeleteAvailabilityBlock,
                ApiOperationIds.CreateAvailabilitySeries,
                ApiOperationIds.DeleteAvailabilitySeries,
                ApiOperationIds.EditAvailabilityOccurrence,
                ApiOperationIds.DeleteAvailabilityOccurrence)),
        new(
            "overlap-search",
            30,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.FindOverlapWindows, ApiOperationIds.GetHomeSuggestions)),
        new(
            "calendar-read",
            120,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.GetCalendarView, ApiOperationIds.GetMyAvailability)),
        new(
            "home-read",
            120,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.GetActionableInbox)),
        new(
            "session-list-read",
            120,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.ListSessions)),
        // PHASE6_DESIGN.md §4.2: four new User-partitioned dimensions, a poll-local grouping
        // distinct from the session split (ticket-P6-S4a). Poll guest ops join the shipped
        // guest-write dimension instead (S4b).
        new(
            "poll-write",
            30,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.CreatePoll,
                ApiOperationIds.CancelPoll,
                ApiOperationIds.DeletePollOption,
                ApiOperationIds.CreatePollGuestLinks,
                ApiOperationIds.RevokePollGuestLink,
                ApiOperationIds.RemovePollGuest,
                // D-186: the poll-local analogue of LeaveSession's place in session-write.
                ApiOperationIds.LeavePoll,
                // D-193: the poll-local analogue of InviteSessionUsers's place in session-write.
                ApiOperationIds.InvitePollUsers)),
        new(
            "poll-attach",
            60,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.FinalizePoll,
                ApiOperationIds.ApprovePollJoinRequest,
                ApiOperationIds.RejectPollJoinRequest,
                ApiOperationIds.RequestPollJoin,
                ApiOperationIds.WithdrawPollJoinRequest,
                ApiOperationIds.ListPollJoinRequests)),
        new(
            "poll-vote",
            60,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.VoteOnPollOption, ApiOperationIds.SuggestPollOption)),
        new(
            "poll-list-read",
            120,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.ListPolls, ApiOperationIds.GetPollFinalizationPreview)),
        // PHASE7_DESIGN.md §4: three new User-partitioned dimensions, 30 -> 33 (disclosed-extension
        // precedent).
        new(
            "notification-read",
            120,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.ListNotifications,
                ApiOperationIds.GetUnreadNotificationCount,
                ApiOperationIds.GetPushConfig)),
        new(
            "notification-write",
            60,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(
                ApiOperationIds.MarkNotificationRead,
                ApiOperationIds.MarkAllNotificationsRead,
                ApiOperationIds.MuteSession,
                ApiOperationIds.UnmuteSession,
                ApiOperationIds.MutePoll,
                ApiOperationIds.UnmutePoll)),
        new(
            "push-subscribe",
            30,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.CreatePushSubscription, ApiOperationIds.DeletePushSubscription)),
        // STEAM_CATALOG_DESIGN.md §3: typeahead traffic must not eat the general read budget.
        new(
            "steam-catalog-search",
            60,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.SearchSteamCatalog)),
        // D-213: the feedback write is the only /api/v1 route reachable with no account, so it gets
        // both dimensions at once -- an anonymous caller is bound by IP alone, an authenticated one
        // by IP and user id simultaneously (User partitions to no-limiter when unauthenticated).
        new(
            "feedback-ip",
            5,
            TimeSpan.FromMinutes(15),
            RateLimitPartitionKind.Ip,
            Operations(ApiOperationIds.SubmitFeedback)),
        new(
            "feedback-user",
            5,
            TimeSpan.FromMinutes(15),
            RateLimitPartitionKind.User,
            Operations(ApiOperationIds.SubmitFeedback)),
        new(
            "default",
            300,
            TimeSpan.FromMinutes(1),
            RateLimitPartitionKind.Ip,
            Operations(),
            CoversEveryApiOperation: true),
    ];

    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(
        ReadOnlySpan<byte> partitionPepper)
    {
        byte[] pepper = partitionPepper.ToArray();
        PartitionedRateLimiter<HttpContext>[] members = Dimensions
            .Select(dimension =>
                PartitionedRateLimiter.Create<HttpContext, string>(
                    context => PartitionFor(context, dimension, pepper)))
            .ToArray();
        return PartitionedRateLimiter.CreateChained<HttpContext>(members);
    }

    public static async ValueTask OnRejectedAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        HttpResponse response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            response.Headers[HeaderNames.RetryAfter] = new StringValues(
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    .ToString(CultureInfo.InvariantCulture));
        }

        await response.WriteAsJsonAsync(
            PartyUpProblemResults.Body(
                context.HttpContext.Request.Path,
                context.HttpContext.TraceIdentifier,
                StatusCodes.Status429TooManyRequests,
                "rate_limited"),
            options: null,
            contentType: "application/problem+json",
            cancellationToken);
    }

    public static IApplicationBuilder UseRateLimitEmailPartitioning(
        this IApplicationBuilder application) =>
        application.Use(async (context, next) =>
        {
            string? operationId = OperationId(context);
            bool needsEmail = Dimensions.Any(dimension =>
                dimension.PartitionKind == RateLimitPartitionKind.Email &&
                operationId is not null &&
                dimension.OperationIds.Contains(operationId));

            if (needsEmail && context.Request.HasJsonContentType())
            {
                context.Request.EnableBuffering(bufferThreshold: 4096, bufferLimit: 65536);
                try
                {
                    using JsonDocument body = await JsonDocument.ParseAsync(
                        context.Request.Body,
                        cancellationToken: context.RequestAborted);
                    if (body.RootElement.TryGetProperty("email", out JsonElement email) &&
                        email.ValueKind == JsonValueKind.String)
                    {
                        context.Items[EmailPartitionItem] =
                            email.GetString()?.Trim().ToUpperInvariant() ?? string.Empty;
                    }
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    context.Items[EmailPartitionItem] = string.Empty;
                }
                finally
                {
                    context.Request.Body.Position = 0;
                }
            }

            await next(context);
        });

    public static string? OperationId(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;

    public static string HashPartitionKey(
        string dimension,
        string value,
        ReadOnlySpan<byte> pepper)
    {
        byte[] prefix = Encoding.UTF8.GetBytes($"{dimension}\0{value}\0");
        byte[] input = new byte[prefix.Length + pepper.Length];
        prefix.CopyTo(input, 0);
        pepper.CopyTo(input.AsSpan(prefix.Length));
        return Convert.ToHexString(SHA256.HashData(input));
    }

    internal static RateLimitPartition<string> PartitionFor(
        HttpContext context,
        RateLimitDimension dimension,
        byte[] pepper)
    {
        string? operationId = OperationId(context);
        bool operationMatchesDimension = dimension.CoversEveryApiOperation
            ? operationId is null || !ClaimedOperationIds.Contains(operationId)
            : operationId is not null && dimension.OperationIds.Contains(operationId);

        if (!operationMatchesDimension)
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        string? rawKey = dimension.PartitionKind switch
        {
            RateLimitPartitionKind.Ip =>
                context.Connection.RemoteIpAddress?.ToString() ?? IPAddress.None.ToString(),
            RateLimitPartitionKind.Email =>
                context.Items.TryGetValue(EmailPartitionItem, out object? email)
                    ? email as string
                    : string.Empty,
            RateLimitPartitionKind.User when context.User.Identity?.IsAuthenticated == true =>
                context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            RateLimitPartitionKind.User => null,
            RateLimitPartitionKind.GuestLink => GuestLinkRouteValue(context)
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? IPAddress.None.ToString(),
            _ => null,
        };

        if (rawKey is null)
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        string partitionKey = HashPartitionKey(dimension.Name, rawKey, pepper);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = dimension.PermitLimit,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                Window = dimension.Window,
            });
    }

    /// <summary>
    /// A-073: the raw <c>{guestLinkId}</c> route value, normalized to lowercase <c>N</c> form —
    /// available because this runs after <c>UseRouting</c>, but before <c>UseAuthorization</c>, so it
    /// cannot validate the guest cookie (that is the point: an unauthenticated caller can consume
    /// another link's bucket, bounded by the <c>default</c> IP ceiling, per A-073's accepted residual).
    /// </summary>
    internal static string? GuestLinkRouteValue(HttpContext context) =>
        context.Request.RouteValues.TryGetValue("guestLinkId", out object? value) &&
        Guid.TryParse(value as string, out Guid guestLinkId)
            ? guestLinkId.ToString("N")
            : null;

    private static HashSet<string> Operations(params string[] operationIds) =>
        new HashSet<string>(operationIds, StringComparer.Ordinal);
}
