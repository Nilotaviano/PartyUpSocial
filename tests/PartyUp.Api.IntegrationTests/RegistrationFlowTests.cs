using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
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

    [Fact]
    public async Task Verify_TwoOutstandingTokensForOneAddressCreateExactlyOneAccount_D016()
    {
        const string email = "two-live-verifications@example.test";
        var friendCodes = new BarrierFriendCodeGenerator();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFriendCodeGenerator>();
                services.AddSingleton<IFriendCodeGenerator>(friendCodes);
            }));
        using HttpClient firstClient = Slice5HttpTest.CreateClient(factory);
        using HttpClient secondClient = Slice5HttpTest.CreateClient(factory);

        Assert.Equal(
            HttpStatusCode.Accepted,
            (await RegisterAsync(firstClient, email)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await RegisterAsync(secondClient, email)).StatusCode);
        IReadOnlyList<EmailMessage> messages = await _factory.EmailSender.WaitForCountAsync(
            message => message.ToAddress == email &&
                message.Template == EmailTemplate.EmailVerification,
            2);
        string[] tokens = messages
            .Select(message => message.Model["Token"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, tokens.Length);
        AntiforgeryPair firstPair = await AntiforgeryTestClient.GetAsync(firstClient);
        AntiforgeryPair secondPair = await AntiforgeryTestClient.GetAsync(secondClient);

        Task<HttpResponseMessage>[] requests =
        [
            VerifyAsync(firstClient, firstPair, tokens[0]),
            VerifyAsync(secondClient, secondPair, tokens[1]),
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        Assert.DoesNotContain(
            responses,
            response => response.StatusCode == HttpStatusCode.InternalServerError);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Single(responses, HasAuthCookie);
        string[] bodies = await Task.WhenAll(
            responses.Select(response => response.Content.ReadAsStringAsync()));
        Assert.Single(
            bodies,
            body => body.Contains("account_exists", StringComparison.Ordinal));

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        const string normalizedEmail = "TWO-LIVE-VERIFICATIONS@EXAMPLE.TEST";
        Guid accountId = await context.AuthUsers
            .Where(user => user.NormalizedEmail == normalizedEmail)
            .Select(user => user.Id)
            .SingleAsync();
        Assert.Equal(1, await context.DomainUsers.CountAsync());
        Assert.Single(
            await context.DomainUsers
                .Where(user => user.Id == UserId.From(accountId))
                .ToListAsync());
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client,
        string email) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new
            {
                email,
                ageGateAttested = true,
                locale = "en",
            }));

    private static async Task<HttpResponseMessage> VerifyAsync(
        HttpClient client,
        AntiforgeryPair antiforgery,
        string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/auth/email/verify")
        {
            Content = JsonContent.Create(new
            {
                token,
                password = "mailbox-owner-password",
                ageGateAttested = true,
            }),
        };
        request.Headers.Add("X-CSRF-TOKEN", antiforgery.Token);
        request.Headers.Add(
            Microsoft.Net.Http.Headers.HeaderNames.Cookie,
            antiforgery.Cookie);
        return await client.SendAsync(request);
    }

    private static bool HasAuthCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues(
            Microsoft.Net.Http.Headers.HeaderNames.SetCookie,
            out IEnumerable<string>? values) &&
        values.Any(value => value.StartsWith("pu.auth=", StringComparison.Ordinal));

    private sealed class BarrierFriendCodeGenerator : IFriendCodeGenerator
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        private int _issued;

        public async Task<FriendCode> NextAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
            {
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
            int suffix = Interlocked.Increment(ref _issued);
            return FriendCode.Create(suffix == 1 ? "ABCDEFGHJA" : "ABCDEFGHJB");
        }
    }
}
