using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RequestLoggingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Log_NeverContainsAQueryString()
    {
        var sink = new CapturingSink();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSerilog((_, logger) => logger
                .MinimumLevel.Information()
                .WriteTo.Sink(sink))));
        using HttpClient client = factory.CreateClient();

        _ = await client.GetAsync("/auth/magic?token=x");

        LogEvent requestLog = Assert.Single(sink.Events, logEvent =>
            logEvent.Properties.TryGetValue("RequestPath", out LogEventPropertyValue? path) &&
            path is ScalarValue { Value: "/auth/magic" });
        Assert.DoesNotContain(
            "token=x",
            requestLog.RenderMessage(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        Assert.DoesNotContain("?", requestLog.Properties["RequestPath"].ToString(), StringComparison.Ordinal);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
