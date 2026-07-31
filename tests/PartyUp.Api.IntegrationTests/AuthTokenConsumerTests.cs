using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using NodaTime;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuthTokenConsumerTests : IAsyncLifetime, IDisposable
{
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TokenConsumers_RejectExpiredReusedAndWrongPurposeTokens_D067()
    {
        var mutationProbe = new MutationProbe();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserAccountGateway>();
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<IUserAccountGateway>(provider =>
                    new ProbingAccountGateway(
                        provider.GetRequiredService<UserAccountGateway>(),
                        mutationProbe));
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);
        TokenConsumer[] consumers =
        [
            new("magic-link", AuthTokenPurpose.MagicLink, "/api/v1/auth/magic-link/consume", HttpStatusCode.Unauthorized, "invalid_or_expired_token", token => new { token, ageGateAttested = true }, false),
            new("password-reset", AuthTokenPurpose.PasswordReset, "/api/v1/auth/password/reset", HttpStatusCode.Conflict, "invalid_or_expired_token", token => new { token, newPassword = "replacement-password" }, false),
            new("email-verification", AuthTokenPurpose.EmailVerification, "/api/v1/auth/email/verify", HttpStatusCode.BadRequest, "invalid_or_expired_token", token => new { token }, false),
            new("email-change-verification", AuthTokenPurpose.EmailChange, "/api/v1/auth/email/change/verify", HttpStatusCode.Conflict, "invalid_or_expired_token", token => new { token }, false),
            new("password-set-proof", AuthTokenPurpose.Reauthentication, "/api/v1/auth/password/set", HttpStatusCode.Conflict, "reauth_required", token => new { newPassword = "replacement-password", reauthToken = token }, true),
            new("email-change-proof", AuthTokenPurpose.Reauthentication, "/api/v1/auth/email/change", HttpStatusCode.Conflict, "reauth_required", token => new { email = "replacement@example.test", reauthToken = token }, true),
        ];

        foreach (TokenConsumer consumer in consumers)
        {
            string email = $"{consumer.Name}@example.test";
            UserId userId = await _factory.SeedUserAsync(email: email, password: Password);
            string? cookie = consumer.Authenticated
                ? await SignInAsync(client, userId)
                : null;
            mutationProbe.Reset();
            int messagesBefore = _factory.EmailSender.Messages.Count;

            IssuedToken expired = await IssueAsync(CreateIssueRequest(
                consumer.Purpose,
                userId,
                email,
                Duration.FromMinutes(1),
                consumer.Name));
            _factory.Clock.Advance(Duration.FromMinutes(2));
            IssuedToken consumed = await IssueAsync(CreateIssueRequest(
                consumer.Purpose,
                userId,
                email,
                Duration.FromHours(1),
                consumer.Name));
            Assert.True((await RedeemAsync(CreateRedeemRequest(
                consumer.Purpose,
                consumed.RawToken))).Succeeded);
            IssuedToken foreign = await IssueAsync(CreateIssueRequest(
                AuthTokenPurpose.DeletionRequestConfirmation,
                userId,
                email,
                Duration.FromHours(1),
                consumer.Name));

            HttpResponseMessage[] responses =
            [
                await PostAsync(client, consumer, expired.RawToken, cookie),
                await PostAsync(client, consumer, consumed.RawToken, cookie),
                await PostAsync(client, consumer, foreign.RawToken, cookie),
            ];
            foreach (HttpResponseMessage response in responses)
            {
                Assert.Equal(consumer.Status, response.StatusCode);
                Assert.Equal(consumer.Code, (await response.Content.ReadFromJsonAsync<AuthProblem>())!.Code);
                Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
            }

            Assert.True(Slice5HttpTest.HeaderNames(responses[0]).SetEquals(Slice5HttpTest.HeaderNames(responses[1])));
            Assert.True(Slice5HttpTest.HeaderNames(responses[0]).SetEquals(Slice5HttpTest.HeaderNames(responses[2])));
            Assert.Equal(0, mutationProbe.Count);
            Assert.Equal(messagesBefore, _factory.EmailSender.Messages.Count);
            Assert.True((await RedeemAsync(new RedeemTokenRequest(
                AuthTokenPurpose.DeletionRequestConfirmation,
                foreign.RawToken))).Succeeded);
        }
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, TokenConsumer consumer, string token, string? cookie) =>
        AntiforgeryTestClient.PostAsync(client, consumer.Route, JsonContent.Create(consumer.Body(token)), cookie);

    private async Task<IssuedToken> IssueAsync(IssueTokenRequest request)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISecurityTokenService>().IssueAsync(request, default);
    }

    private async Task<TokenRedemption> RedeemAsync(RedeemTokenRequest request)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISecurityTokenService>().RedeemAsync(request, default);
    }

    private static IssueTokenRequest CreateIssueRequest(AuthTokenPurpose purpose, UserId userId, string email, Duration ttl, string name) =>
        purpose switch
        {
            AuthTokenPurpose.MagicLink or AuthTokenPurpose.PasswordReset or AuthTokenPurpose.EmailVerification =>
                new(purpose, userId, email.ToUpperInvariant(), ttl, Email: email),
            AuthTokenPurpose.EmailChange =>
                new(purpose, userId, $"CHANGED-{name.ToUpperInvariant()}@EXAMPLE.TEST", ttl, Email: $"changed-{name}@example.test"),
            AuthTokenPurpose.Reauthentication => new(purpose, userId, null, ttl),
            AuthTokenPurpose.DeletionRequestConfirmation =>
                new(purpose, userId, email.ToUpperInvariant(), ttl, Email: email),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
        };

    private static RedeemTokenRequest CreateRedeemRequest(AuthTokenPurpose purpose, string rawToken) =>
        new(purpose, rawToken, AgeGateAttested: purpose is AuthTokenPurpose.MagicLink or AuthTokenPurpose.EmailVerification ? true : null);

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(client, $"/api/v1/_slice2/sign-in/{userId.Value:D}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private sealed record TokenConsumer(string Name, AuthTokenPurpose Purpose, string Route, HttpStatusCode Status, string Code, Func<string, object> Body, bool Authenticated);

    private sealed class MutationProbe
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        internal void Record() => Interlocked.Increment(ref _count);
        internal void Reset() => Volatile.Write(ref _count, 0);
    }

    private sealed class ProbingAccountGateway(UserAccountGateway inner, MutationProbe probe) : IUserAccountGateway
    {
        public Task<UserId?> FindUserIdByEmailAsync(string email, CancellationToken ct) => inner.FindUserIdByEmailAsync(email, ct);
        public Task<UserId?> FindUserIdByLoginAsync(string provider, string key, CancellationToken ct) => inner.FindUserIdByLoginAsync(provider, key, ct);
        public Task<PasswordCheckResult> CheckPasswordAsync(UserId id, string password, CancellationToken ct) => inner.CheckPasswordAsync(id, password, ct);
        public Task VerifyDummyPasswordAsync(CancellationToken ct) => inner.VerifyDummyPasswordAsync(ct);
        public Task<AccountEmailInfo?> GetEmailAsync(UserId id, CancellationToken ct) => inner.GetEmailAsync(id, ct);
        public Task<IReadOnlyList<string>> ListLoginProvidersAsync(UserId id, CancellationToken ct) => inner.ListLoginProvidersAsync(id, ct);
        public Task CreateAccountAsync(UserId id, string? email, bool confirmed, string? password, CancellationToken ct) => Mutate(() => inner.CreateAccountAsync(id, email, confirmed, password, ct));
        public Task SetPasswordAsync(UserId id, string password, CancellationToken ct) => Mutate(() => inner.SetPasswordAsync(id, password, ct));
        public Task RemovePasswordAsync(UserId id, CancellationToken ct) => Mutate(() => inner.RemovePasswordAsync(id, ct));
        public Task SetEmailAsync(UserId id, string email, bool confirmed, CancellationToken ct) => Mutate(() => inner.SetEmailAsync(id, email, confirmed, ct));
        public Task ConfirmEmailAsync(UserId id, CancellationToken ct) => Mutate(() => inner.ConfirmEmailAsync(id, ct));
        public Task AddLoginAsync(UserId id, string provider, string key, CancellationToken ct) => Mutate(() => inner.AddLoginAsync(id, provider, key, ct));
        public Task RemoveLoginAsync(UserId id, string provider, CancellationToken ct) => Mutate(() => inner.RemoveLoginAsync(id, provider, ct));
        public Task PurgeCredentialsAsync(UserId id, CancellationToken ct) => Mutate(() => inner.PurgeCredentialsAsync(id, ct));
        public Task EnsureRoleExistsAsync(string role, CancellationToken ct) => Mutate(() => inner.EnsureRoleExistsAsync(role, ct));
        public Task<bool> AddToRoleAsync(UserId id, string role, CancellationToken ct) { probe.Record(); return inner.AddToRoleAsync(id, role, ct); }
        private Task Mutate(Func<Task> action) { probe.Record(); return action(); }
    }
}
