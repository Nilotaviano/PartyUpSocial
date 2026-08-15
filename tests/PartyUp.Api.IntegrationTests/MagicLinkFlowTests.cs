using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;
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

    [Theory]
    [InlineData("/u/Player.Name", "/u/player.name")]
    [InlineData("/add/2345-6789-AB", "/add/23456789AB")]
    public async Task RequestThenConsume_RoundTripsOnlyCanonicalFriendContinuation(
        string requested,
        string expected)
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string email = $"magic-continuation-{Guid.NewGuid():N}@example.test";
        HttpResponseMessage request = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link",
            JsonContent.Create(new
            {
                email,
                locale = "es",
                continuation = requested,
            }));
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        EmailMessage message = await _factory.EmailSender.WaitForAsync(
            candidate => candidate.ToAddress == email &&
                candidate.Template == EmailTemplate.MagicLink);
        Assert.Equal(
            $"&next={Uri.EscapeDataString(expected)}",
            message.Model["Continuation"]);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new
            {
                token = message.Model["Token"],
                ageGateAttested = true,
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SessionResponse session = (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
        Assert.Equal(expected, session.Continuation);
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

    [Fact]
    public async Task Consume_TwoConcurrentRedemptionsForTheSameAccount_BothSucceedWithNo5xx_D067()
    {
        const string email = "magic-race@example.test";
        UserId userId = await _factory.SeedUserAsync(email: email, emailConfirmed: false);
        using HttpClient requestClient = Slice5HttpTest.CreateClient(_factory);
        string firstToken = await RequestAsync(requestClient, email);
        string secondToken = await RequestAsync(requestClient, email);

        var barrier = new ConfirmEmailBarrier();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserStore<AuthUser>>();
                services.AddScoped<IUserStore<AuthUser>>(provider =>
                    new BarrierUserStore(
                        provider.GetRequiredService<AppDbContext>(),
                        barrier));
            }));

        using HttpClient firstClient = Slice5HttpTest.CreateClient(factory);
        using HttpClient secondClient = Slice5HttpTest.CreateClient(factory);
        Task<HttpResponseMessage> first = AntiforgeryTestClient.PostAsync(
            firstClient,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token = firstToken, ageGateAttested = true }));
        Task<HttpResponseMessage> second = AntiforgeryTestClient.PostAsync(
            secondClient,
            "/api/v1/auth/magic-link/consume",
            JsonContent.Create(new { token = secondToken, ageGateAttested = true }));
        HttpResponseMessage[] responses = await Task.WhenAll(first, second);

        // Asserted before the status codes: a barrier that gave up is the test failing to overlap the
        // two redemptions, and it surfaces as a 500 that looks exactly like the server-side defect
        // this test exists to catch. Naming it keeps the two apart.
        Assert.False(
            barrier.TimedOut,
            "the two redemptions never overlapped inside ConfirmEmailAsync; the barrier timed out.");
        Assert.Equal(2, barrier.Arrivals);
        foreach (HttpResponseMessage response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(
                response.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
        }

        await using AsyncServiceScope verificationScope = factory.Services.CreateAsyncScope();
        AppDbContext context = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool confirmed = await context.AuthUsers
            .Where(user => user.Id == userId.Value)
            .Select(user => user.EmailConfirmed)
            .SingleAsync();
        Assert.True(confirmed);

        List<Instant?> consumedAt = await context.Database
            .SqlQueryRaw<Instant?>(
                """
                SELECT consumed_at AS "Value"
                  FROM auth_tokens
                 WHERE purpose = {0}
                   AND user_id = {1}
                """,
                (int)AuthTokenPurpose.MagicLink,
                userId.Value)
            .ToListAsync();
        Assert.Equal(2, consumedAt.Count);
        Assert.All(consumedAt, value => Assert.NotNull(value));
    }

    private async Task<string> RequestAsync(HttpClient client, string email)
    {
        bool Matches(EmailMessage candidate) =>
            candidate.ToAddress == email && candidate.Template == EmailTemplate.MagicLink;

        // The magic-link mail is dispatched after the 202 is written, so waiting for "a" matching
        // message would hand back the message a previous request for the same address already left
        // in the capture — two requests then yield the same token, which is a different test than
        // the one the caller wrote. Waiting for one more than are already there is the token this
        // call just asked for.
        int alreadyCaptured = _factory.EmailSender.Messages.Count(Matches);
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/magic-link",
            JsonContent.Create(new { email, locale = "es-ES" }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        IReadOnlyList<EmailMessage> messages = await _factory.EmailSender.WaitForCountAsync(
            Matches,
            alreadyCaptured + 1);
        return messages[^1].Model["Token"];
    }

    private sealed class ConfirmEmailBarrier
    {
        private readonly TaskCompletionSource _bothArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        private int _timedOut;

        internal int Arrivals => Volatile.Read(ref _arrivals);

        internal bool TimedOut => Volatile.Read(ref _timedOut) != 0;

        internal async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothArrived.TrySetResult();
            }

            try
            {
                await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (TimeoutException)
            {
                Volatile.Write(ref _timedOut, 1);
                throw;
            }
        }
    }

    private sealed class BarrierUserStore
        : UserStore<AuthUser, AuthRole, AppDbContext, Guid>
    {
        private readonly AppDbContext _context;
        private readonly ConfirmEmailBarrier _barrier;

        internal BarrierUserStore(AppDbContext context, ConfirmEmailBarrier barrier)
            : base(context)
        {
            _context = context;
            _barrier = barrier;
        }

        public override async Task<AuthUser?> FindByIdAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            // Only the in-transaction read inside ConsumeMagicLinkUseCase's ConfirmEmailAsync should
            // gate on the barrier — AuthenticateAndSignIn's post-commit FindByIdAsync runs with no
            // ambient transaction and must not be held up, or the sign-in half of the request never
            // completes and the barrier's own 10s timeout trips instead of the real assertions.
            if (_context.Database.CurrentTransaction is not null)
            {
                await _barrier.ArriveAsync(cancellationToken);
            }

            return await base.FindByIdAsync(userId, cancellationToken);
        }
    }
}
