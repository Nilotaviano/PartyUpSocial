using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Application.Email;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ChangeEmailFlowTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ChangeEmail_RequestThenVerify_PreservesTheMailboxAddressCase()
    {
        const string password = "mailbox-owner-password";
        var userId = await _factory.SeedUserAsync(
            email: "old@example.test",
            password: password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        HttpResponseMessage login = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new
            {
                email = "old@example.test",
                password,
            }));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        string cookie = Slice5HttpTest.AuthCookie(login);

        const string newEmail = "NewAddress@corp.example";
        HttpResponseMessage requested = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/change",
            JsonContent.Create(new
            {
                email = newEmail,
                currentPassword = password,
            }),
            cookie);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);

        EmailMessage verification = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == newEmail &&
                message.Template == EmailTemplate.EmailChangeVerification);
        HttpResponseMessage verified = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/change/verify",
            JsonContent.Create(new { token = verification.Model["Token"] }));
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string stored = await context.AuthUsers
            .Where(user => user.Id == userId.Value)
            .Select(user => user.Email!)
            .SingleAsync();
        Assert.Equal(newEmail, stored);
        Assert.Equal(newEmail, verification.ToAddress);
    }
}
