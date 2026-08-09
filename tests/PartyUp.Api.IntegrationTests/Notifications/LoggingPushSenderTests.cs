using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PartyUp.Application.Push;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Push;

namespace PartyUp.Api.IntegrationTests.Notifications;

/// <summary>
/// PHASE7_DESIGN.md §3's mail-drop analogue, unit-level (no Docker/Postgres — pure filesystem, the same
/// scope <see cref="RecurringJobRegistrationServiceRetryTests"/> already carries without an Integration
/// trait): restricted to Development exactly like <c>LoggingEmailSender</c>, and the drop file's shape
/// never carries the endpoint or keys (A-015).
/// </summary>
public sealed class LoggingPushSenderTests : IDisposable
{
    private readonly string _dropPath = Path.Combine(
        Path.GetTempPath(), "partyup-push-drop-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dropPath))
        {
            try
            {
                Directory.Delete(_dropPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task SendAsync_OutsideDevelopment_Throws()
    {
        var sender = CreateSender(isDevelopment: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sender.SendAsync(SampleTarget(), SamplePayload(), default));
    }

    [Fact]
    public async Task SendAsync_InDevelopment_ReturnsDeliveredAndIsAlwaysConfigured()
    {
        var sender = CreateSender(isDevelopment: true);

        Assert.True(sender.IsConfigured);
        PushSendResult result = await sender.SendAsync(SampleTarget(), SamplePayload(), default);

        Assert.Equal(PushSendOutcome.Delivered, result.Outcome);
    }

    /// <summary>
    /// The drop file is the S6 browser-lane assertion target: it must carry exactly the wire payload
    /// plus the subscription id — never the endpoint, the P-256 key or the auth secret.
    /// </summary>
    [Fact]
    public async Task SendAsync_WritesOneDropFile_WithNoCredentialFields()
    {
        var sender = CreateSender(isDevelopment: true);
        PushSubscriptionTarget target = SampleTarget();
        PushPayload payload = SamplePayload();

        await sender.SendAsync(target, payload, default);

        string[] files = Directory.GetFiles(_dropPath);
        string content = Assert.Single(files) is { } path ? await File.ReadAllTextAsync(path) : string.Empty;

        Assert.Contains(target.Id.Value.ToString(), content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(payload.Title, content, StringComparison.Ordinal);
        Assert.Contains(payload.Body, content, StringComparison.Ordinal);
        Assert.Contains(payload.Url, content, StringComparison.Ordinal);
        Assert.Contains(payload.Tag, content, StringComparison.Ordinal);

        Assert.DoesNotContain(target.Endpoint, content, StringComparison.Ordinal);
        Assert.DoesNotContain(target.PublicKey, content, StringComparison.Ordinal);
        Assert.DoesNotContain(target.AuthSecret, content, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publicKey", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authSecret", content, StringComparison.OrdinalIgnoreCase);
    }

    private LoggingPushSender CreateSender(bool isDevelopment)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Push:DropPath"] = _dropPath })
            .Build();

        return new LoggingPushSender(
            new LoggingPushSenderEnvironment(isDevelopment, Path.GetTempPath()),
            configuration,
            NullLogger<LoggingPushSender>.Instance);
    }

    private static PushSubscriptionTarget SampleTarget() => new(
        PushSubscriptionId.New(),
        "https://push.example/endpoint-should-never-appear",
        "public-key-should-never-appear",
        "auth-secret-should-never-appear");

    private static PushPayload SamplePayload() => new(
        "Session confirmed",
        "Board Game Night is confirmed.",
        "/sessions/00000000-0000-0000-0000-000000000000",
        "session:00000000-0000-0000-0000-000000000000");
}
