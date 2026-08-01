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

namespace PartyUp.Api.Security;

public enum RateLimitPartitionKind
{
    Ip,
    Email,
    User,
}

public sealed record RateLimitDimension(
    string Name,
    int PermitLimit,
    TimeSpan Window,
    RateLimitPartitionKind PartitionKind,
    IReadOnlySet<string> OperationIds,
    bool CoversEveryApiOperation = false);

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

    public static IReadOnlyList<RateLimitDimension> Dimensions { get; } =
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
            5,
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

    private static RateLimitPartition<string> PartitionFor(
        HttpContext context,
        RateLimitDimension dimension,
        byte[] pepper)
    {
        string? operationId = OperationId(context);
        if (!dimension.CoversEveryApiOperation &&
            (operationId is null || !dimension.OperationIds.Contains(operationId)))
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

    private static HashSet<string> Operations(params string[] operationIds) =>
        new HashSet<string>(operationIds, StringComparer.Ordinal);
}
