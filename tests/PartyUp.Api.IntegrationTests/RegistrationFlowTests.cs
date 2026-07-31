using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RegistrationFlowTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Register_ThenCompleteOnboarding_ActivatesTheAccount_D127()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        const string email = "registration-flow@example.test";
        HttpResponseMessage registration = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new
            {
                email,
                ageGateAttested = true,
                locale = "pt-br",
            }));
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

        EmailMessage verification = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == email &&
                message.Template == EmailTemplate.EmailVerification);
        string token = verification.Model["Token"];
        HttpResponseMessage verified = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new { token, password = "mailbox-owner-password" }));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        string cookie = Slice5HttpTest.AuthCookie(verified);

        HttpResponseMessage completed = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/onboarding/complete",
            JsonContent.Create(new
            {
                username = "FlowPlayer",
                displayName = "Flow Player",
                avatar = new { kind = "emoji", emoji = "🙂" },
                timeZone = "America/Fortaleza",
                locale = "pt-BR",
            }),
            cookie);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        SessionResponse session = (await completed.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.Equal("active", session.State);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await context.DomainUsers
            .Where(user => user.Lifecycle == PartyUp.Domain.Kernel.UserLifecycle.Active)
            .ToListAsync());
    }

    [Fact]
    public async Task RegisterThenVerify_PreservesTheMailboxAddressCase_D016()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        const string email = "Registration.Flow@Example.Test";
        HttpResponseMessage registration = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new
            {
                email,
                ageGateAttested = true,
                locale = "en",
            }));
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

        EmailMessage verification = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == email &&
                message.Template == EmailTemplate.EmailVerification);
        HttpResponseMessage verified = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new
            {
                token = verification.Model["Token"],
                password = "mailbox-owner-password",
                ageGateAttested = true,
            }));
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string stored = await context.AuthUsers
            .Where(user => user.NormalizedEmail == "REGISTRATION.FLOW@EXAMPLE.TEST")
            .Select(user => user.Email!)
            .SingleAsync();
        Assert.Equal(email, stored);
        Assert.Equal(email, verification.ToAddress);
    }

    [Fact]
    public async Task VerifyEmail_AttestedRetryWithTheSameTokenSucceeds_D083()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        const string email = "AgeGate.Retry@Example.Test";
        IssuedToken issued;
        await using (AsyncServiceScope issueScope = _factory.Services.CreateAsyncScope())
        {
            var tokens = issueScope.ServiceProvider
                .GetRequiredService<ISecurityTokenService>();
            issued = await tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.EmailVerification,
                    null,
                    "AGEGATE.RETRY@EXAMPLE.TEST",
                    Duration.FromHours(24),
                    new PendingRegistration("en", false),
                    Email: email),
                default);
        }

        HttpResponseMessage refused = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new
            {
                token = issued.RawToken,
                password = "mailbox-owner-password",
            }));
        HttpResponseMessage retried = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new
            {
                token = issued.RawToken,
                password = "mailbox-owner-password",
                ageGateAttested = true,
            }));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }
}
