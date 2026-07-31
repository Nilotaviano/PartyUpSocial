using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Application.Email;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RegistrationEnumerationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Register_ForAKnownAndAnUnknownAddress_AreByteIdentical_D016()
    {
        const string known = "known-registration@example.test";
        const string unknown = "unknown-registration@example.test";
        await _factory.SeedUserAsync(email: known);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        int before = await TokenCountAsync();
        HttpResponseMessage knownResponse = await RegisterAsync(client, known);
        int afterKnown = await TokenCountAsync();
        HttpResponseMessage unknownResponse = await RegisterAsync(client, unknown);
        int afterUnknown = await TokenCountAsync();
        EmailMessage knownMail = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == known);
        EmailMessage unknownMail = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == unknown);

        Assert.Equal(knownResponse.StatusCode, unknownResponse.StatusCode);
        Assert.Equal(
            await knownResponse.Content.ReadAsByteArrayAsync(),
            await unknownResponse.Content.ReadAsByteArrayAsync());
        Assert.True(Slice5HttpTest.HeaderNames(knownResponse)
            .SetEquals(Slice5HttpTest.HeaderNames(unknownResponse)));
        Assert.False(knownResponse.Headers.Contains(HeaderNames.SetCookie));
        Assert.False(unknownResponse.Headers.Contains(HeaderNames.SetCookie));
        Assert.Equal(1, afterKnown - before);
        Assert.Equal(1, afterUnknown - afterKnown);
        Assert.Equal(EmailTemplate.ExistingAccountNotice, knownMail.Template);
        Assert.Equal(EmailTemplate.EmailVerification, unknownMail.Template);
    }

    [Fact]
    public async Task Register_WithAPasswordField_Returns400_D016()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new
            {
                email = "bad-shape@example.test",
                password = "must-not-be-here",
                ageGateAttested = true,
                locale = "en",
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new { email, ageGateAttested = true, locale = "en" }));

    private async Task<int> TokenCountAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Database
            .SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM auth_tokens""")
            .SingleAsync();
    }
}
