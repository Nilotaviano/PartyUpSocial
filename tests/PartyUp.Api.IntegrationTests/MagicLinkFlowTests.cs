using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Application.Email;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class MagicLinkFlowTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RequestThenConsume_SignsIn_D067()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string token = await RequestAsync(client, "magic-flow@example.test");

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token, ageGateAttested = true }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Consume_Twice_Fails_D067()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string token = await RequestAsync(client, "magic-replay@example.test");

        HttpResponseMessage first = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token, ageGateAttested = true }));
        HttpResponseMessage second = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token, ageGateAttested = true }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task RequestThenConsume_PreservesTheMailboxAddressCase()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        const string email = "Magic.Flow@Example.Test";
        string token = await RequestAsync(client, email);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token, ageGateAttested = true }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        EmailMessage outbound = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == email &&
                message.Template == EmailTemplate.MagicLink);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string stored = await context.AuthUsers
            .Where(user => user.NormalizedEmail == "MAGIC.FLOW@EXAMPLE.TEST")
            .Select(user => user.Email!)
            .SingleAsync();
        Assert.Equal(email, stored);
        Assert.Equal(email, outbound.ToAddress);
    }

    private async Task<string> RequestAsync(HttpClient client, string email)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link",
            JsonContent.Create(new { email, locale = "es-ES" }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        EmailMessage message = await _factory.EmailSender.WaitForAsync(
            candidate => candidate.ToAddress == email &&
                candidate.Template == EmailTemplate.MagicLink);
        return message.Model["Token"];
    }
}
