using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PartyUp.Api.Auth;

namespace PartyUp.Api.IntegrationTests;

public sealed class ExternalAuthOriginOptionsTests
{
    [Fact]
    public void ExplicitMismatchedSteamRealm_WinsAndLogsAWarning()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicBaseUrl"] = "https://partyup.social/app",
                ["Steam:Realm"] = "https://signin.example.test/",
            })
            .Build();
        var logger = new RecordingLogger();

        var options = new ExternalAuthOriginOptions(configuration, logger);

        Assert.Equal("https://partyup.social", options.PublicOrigin);
        Assert.Equal("https://signin.example.test", options.SteamRealm);
        string warning = Assert.Single(logger.Warnings);
        Assert.Contains("signin.example.test", warning, StringComparison.Ordinal);
        Assert.Contains("partyup.social", warning, StringComparison.Ordinal);
    }

    private sealed class RecordingLogger : ILogger<ExternalAuthOriginOptions>
    {
        internal List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
