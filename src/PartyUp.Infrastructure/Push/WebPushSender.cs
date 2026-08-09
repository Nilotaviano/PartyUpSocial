using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Configuration;
using PartyUp.Application.Push;

namespace PartyUp.Infrastructure.Push;

/// <summary>
/// The RFC 8291 <c>aes128gcm</c> + RFC 8292 VAPID adapter (PHASE7_DESIGN.md §3, A-013 unlock). Reads
/// <c>Push:Vapid:PublicKey</c>/<c>PrivateKey</c>/<c>Subject</c> once at construction: absent or partial
/// configuration leaves <see cref="IsConfigured"/> <see langword="false"/> rather than throwing, which
/// is what lets the dispatch job no-op cleanly (§8) instead of failing every tick.
/// </summary>
public sealed class WebPushSender : IPushSender
{
    private readonly PushServiceClient _client = new();

    public WebPushSender(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? publicKey = configuration["Push:Vapid:PublicKey"];
        string? privateKey = configuration["Push:Vapid:PrivateKey"];
        string? subject = configuration["Push:Vapid:Subject"];

        if (!string.IsNullOrWhiteSpace(publicKey)
            && !string.IsNullOrWhiteSpace(privateKey)
            && !string.IsNullOrWhiteSpace(subject))
        {
            _client.DefaultAuthentication = new VapidAuthentication(publicKey, privateKey)
            {
                Subject = subject,
            };
            IsConfigured = true;
        }
    }

    public bool IsConfigured { get; }

    /// <summary>
    /// §3's eviction rule, verbatim: 404/410 → <c>Gone</c> (the caller revokes the subscription); every
    /// other non-success status, and any transport-level failure that never reached the push service at
    /// all, → <c>Failed</c> with no retry. Never logs or persists <paramref name="target"/>'s endpoint or
    /// keys, or <paramref name="payload"/>'s text — only the translated outcome and status code leave
    /// this method.
    /// </summary>
    public async Task<PushSendResult> SendAsync(
        PushSubscriptionTarget target,
        PushPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payload);

        var subscription = new Lib.Net.Http.WebPush.PushSubscription { Endpoint = target.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, target.PublicKey);
        subscription.SetKey(PushEncryptionKeyName.Auth, target.AuthSecret);

        string json = JsonSerializer.Serialize(new
        {
            title = payload.Title,
            body = payload.Body,
            url = payload.Url,
            tag = payload.Tag,
        });
        var message = new PushMessage(json);

        try
        {
            await _client.RequestPushMessageDeliveryAsync(subscription, message, cancellationToken);
            return new PushSendResult(PushSendOutcome.Delivered, (int)HttpStatusCode.Created);
        }
        catch (PushServiceClientException exception)
        {
            int statusCode = (int)exception.StatusCode;
            PushSendOutcome outcome = statusCode is 404 or 410 ? PushSendOutcome.Gone : PushSendOutcome.Failed;
            return new PushSendResult(outcome, statusCode);
        }
        catch (HttpRequestException)
        {
            return new PushSendResult(PushSendOutcome.Failed, null);
        }
    }
}
