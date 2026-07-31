using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(RequestLoggingTestGroup.Name)]
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
            builder.ConfigureServices(services => services.AddSerilog((provider, logger) => logger
                .ReadFrom.Configuration(provider.GetRequiredService<IConfiguration>())
                .WriteTo.Sink(sink))));
        using HttpClient client = factory.CreateClient();

        _ = await client.GetAsync("/auth/magic?token=x");

        LogEvent[] requestLogs = sink.Events.Where(logEvent =>
            logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
            source is ScalarValue { Value: "Serilog.AspNetCore.RequestLoggingMiddleware" } &&
            logEvent.Properties.TryGetValue("RequestPath", out LogEventPropertyValue? path) &&
            path is ScalarValue { Value: "/auth/magic" }).ToArray();
        Assert.NotEmpty(requestLogs);
        Assert.DoesNotContain(sink.Events, logEvent =>
            logEvent.RenderMessage(CultureInfo.InvariantCulture).Contains(
                "token=x",
                StringComparison.Ordinal));
        Assert.All(requestLogs, requestLog =>
            Assert.DoesNotContain(
                "?",
                requestLog.Properties["RequestPath"].ToString(),
                StringComparison.Ordinal));
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RequestLoggingTestGroup
{
    public const string Name = "Request logging";
}
