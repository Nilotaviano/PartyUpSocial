using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Application.Email;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ForgotPasswordEnumerationTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Forgot_ForAKnownAndAnUnknownAddress_AreIndistinguishable_D067()
    {
        const string known = "known-forgot@example.test";
        const string unknown = "unknown-forgot@example.test";
        await _factory.SeedUserAsync(email: known);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        int before = await TokenCountAsync();
        HttpResponseMessage knownResponse = await ForgotAsync(client, known);
        int afterKnown = await TokenCountAsync();
        HttpResponseMessage unknownResponse = await ForgotAsync(client, unknown);
        int afterUnknown = await TokenCountAsync();
        EmailMessage knownMail = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == known);
        EmailMessage unknownMail = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == unknown);

        Assert.Equal(HttpStatusCode.Accepted, knownResponse.StatusCode);
        Assert.Equal(knownResponse.StatusCode, unknownResponse.StatusCode);
        Assert.Equal(
            await knownResponse.Content.ReadAsByteArrayAsync(),
            await unknownResponse.Content.ReadAsByteArrayAsync());
        Assert.True(Slice5HttpTest.HeaderNames(knownResponse)
            .SetEquals(Slice5HttpTest.HeaderNames(unknownResponse)));
        Assert.Equal(EmailTemplate.PasswordReset, knownMail.Template);
        Assert.Equal(EmailTemplate.PasswordResetNoAccount, unknownMail.Template);
        Assert.Equal(1, afterKnown - before);
        Assert.Equal(1, afterUnknown - afterKnown);
    }

    private static Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/forgot",
            JsonContent.Create(new { email }));

    private async Task<int> TokenCountAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Database
            .SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM auth_tokens""")
            .SingleAsync();
    }
}
