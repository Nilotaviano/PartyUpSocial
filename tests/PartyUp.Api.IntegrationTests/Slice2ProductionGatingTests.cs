using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// The four <c>/_slice2</c> test endpoints are registered inside
/// <c>if (app.Environment.IsEnvironment("Testing"))</c>, which makes them a security carve-out that
/// only holds as long as the gate holds: outside the Testing environment none of them may route.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Slice2ProductionGatingTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task NoSlice2RouteExistsOutsideTheTestingEnvironment()
    {
        await using var production = _factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using HttpClient client = production.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
                BaseAddress = new Uri("https://localhost"),
            });

        Assert.Equal(
            "Production",
            production.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/_slice2/authenticated")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/_slice2/onboarded")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await AntiforgeryTestClient.PostAsync(
                client, $"/api/v1/_slice2/sign-in/{Guid.NewGuid():D}", content: null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await AntiforgeryTestClient.PostAsync(client, "/api/v1/_slice2/sign-out", content: null)).StatusCode);
    }
}
