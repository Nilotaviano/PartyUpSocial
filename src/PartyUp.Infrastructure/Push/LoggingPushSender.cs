using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PartyUp.Application.Push;

namespace PartyUp.Infrastructure.Push;

/// <summary>
/// The mail-drop analogue for push (PHASE7_DESIGN.md §3): restricted to Development exactly like
/// <c>LoggingEmailSender</c> (<c>LoggingEmailSender.cs:15-19</c>), and writes one JSON file per send to
/// <c>Push:DropPath</c> (default <c>push-drop/</c>) — the browser lane's (S6) trigger-seam assertion
/// target. The file carries only the wire payload plus the subscription id, never the endpoint or keys
/// (A-015): the whole point of a drop file is to be safe to read back in a test.
/// </summary>
public sealed partial class LoggingPushSender(
    LoggingPushSenderEnvironment environment,
    IConfiguration configuration,
    ILogger<LoggingPushSender> logger) : IPushSender
{
    public bool IsConfigured => true;

    public async Task<PushSendResult> SendAsync(
        PushSubscriptionTarget target,
        PushPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payload);

        if (!environment.IsDevelopment)
        {
            throw new InvalidOperationException(
                "The logging push sender is restricted to the Development environment.");
        }

        LogDevelopmentPush(logger, target.Id.Value, payload.Tag, payload.Title);

        string dropPath = configuration["Push:DropPath"]
            ?? Path.Combine(environment.ContentRootPath, "push-drop");
        try
        {
            Directory.CreateDirectory(dropPath);
            string fileName = $"{Guid.CreateVersion7():N}.json";
            string json = JsonSerializer.Serialize(new
            {
                subscriptionId = target.Id.Value,
                title = payload.Title,
                body = payload.Body,
                url = payload.Url,
                tag = payload.Tag,
            });
            await File.WriteAllTextAsync(Path.Combine(dropPath, fileName), json, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            LogDropPathNotWritable(logger, exception);
        }

        return new PushSendResult(PushSendOutcome.Delivered, null);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Development push to subscription {SubscriptionId} (tag {Tag}): {Title}")]
    private static partial void LogDevelopmentPush(
        ILogger logger,
        Guid subscriptionId,
        string tag,
        string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The development push drop path is not writable")]
    private static partial void LogDropPathNotWritable(ILogger logger, Exception exception);
}

public sealed record LoggingPushSenderEnvironment(bool IsDevelopment, string ContentRootPath);
