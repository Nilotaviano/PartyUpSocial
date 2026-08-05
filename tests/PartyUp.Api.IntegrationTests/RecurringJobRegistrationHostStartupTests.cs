using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PartyUp.Api.Hosting;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RecurringJobRegistrationHostStartupTests
{
    // Regression coverage for the CI failure fixed alongside this test: a host booted in a
    // non-"Testing" environment (recurring-job registration is only skipped in "Testing") whose
    // configured Db connection is unreachable must still finish starting and serve requests. Before
    // the fix, Hangfire recurring-job registration ran synchronously on the startup path and any
    // NpgsqlException there took the whole host down with it. Other hosted services (AdminSeeder,
    // deletion sweep, email dispatch) are removed here so this test isolates the one behavior it
    // exists to prove; none of them are in this ticket's scope.
    [Fact]
    public async Task HostStarts_AndServesRequests_WhenConfiguredDbIsUnreachable()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                webHost.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Db"] =
                            "Host=127.0.0.1;Port=59999;Database=unreachable;Username=none;" +
                            "Password=none;Timeout=1;Command Timeout=1",
                        ["Database:MigrateOnStartup"] = "false",
                    }));
                webHost.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService<RecurringJobRegistrationService>();
                });
            });

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/auth/antiforgery",
            timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
