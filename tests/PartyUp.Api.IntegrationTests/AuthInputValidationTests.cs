using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure.Auth;
using PartyUp.Infrastructure.Identity;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class AuthInputValidationTests : IAsyncLifetime, IDisposable
{
    private const string Email = "validation-user@example.test";
    private const string Password = "correct-password";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AuthRoutes_MalformedBodiesReturn400WithoutSideEffects()
    {
        UserId userId = await _factory.SeedUserAsync(email: Email, password: Password);
        var accountProbe = new MutationProbe();
        var tokenProbe = new MutationProbe();
        await using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserAccountGateway>();
                services.AddScoped<UserAccountGateway>();
                services.AddScoped<IUserAccountGateway>(provider =>
                    new ProbingAccountGateway(
                        provider.GetRequiredService<UserAccountGateway>(),
                        accountProbe));
                services.RemoveAll<ISecurityTokenService>();
                services.AddScoped<SecurityTokenService>();
                services.AddScoped<ISecurityTokenService>(provider =>
                    new ProbingTokenService(
                        provider.GetRequiredService<SecurityTokenService>(),
                        tokenProbe));
            }));
        using HttpClient client = Slice5HttpTest.CreateClient(factory);
        string cookie = await SignInAsync(client, userId);
        accountProbe.Reset();
        tokenProbe.Reset();
        int messagesBefore = _factory.EmailSender.Messages.Count;
        InvalidCase[] cases =
        [
            JsonCase("/api/v1/auth/register", new { email = "not-an-email", ageGateAttested = true, locale = "en" }),
            JsonCase("/api/v1/auth/login", new { email = Email, password = 42 }),
            JsonCase("/api/v1/auth/magic-link", new { email = "not-an-email", locale = "en" }),
            JsonCase("/api/v1/auth/magic-link/consume", new { token = 42, ageGateAttested = true }),
            JsonCase("/api/v1/auth/password/forgot", new { email = "not-an-email" }),
            JsonCase("/api/v1/auth/password/reset", new { token = "x", newPassword = "short" }),
            JsonCase("/api/v1/auth/password/change", new { currentPassword = Password, newPassword = "short" }),
            JsonCase("/api/v1/auth/password/set", new { newPassword = "short", currentPassword = Password }),
            JsonCase("/api/v1/auth/email/change", new { email = "not-an-email", currentPassword = Password }),
            JsonCase("/api/v1/auth/email/change/verify", new { token = 42 }),
            MalformedCase("/api/v1/auth/email/verify/request"),
            JsonCase("/api/v1/auth/email/verify", new { token = 42, password = Password }),
            JsonCase("/api/v1/auth/reauth", new { currentPassword = 42 }),
            MalformedCase("/api/v1/auth/reauth/email"),
            MalformedCase("/api/v1/auth/logout"),
        ];

        var responses = new List<(string Route, HttpResponseMessage Response)>();
        foreach (InvalidCase invalid in cases)
        {
            responses.Add((
                invalid.Route,
                await AntiforgeryTestClient.PostAsync(
                    client,
                    invalid.Route,
                    invalid.Content(),
                    cookie)));
        }

        foreach ((string route, HttpResponseMessage response) in responses)
        {
            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"{route} returned {(int)response.StatusCode} instead of 400.");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
            Assert.False(response.Headers.Contains(HeaderNames.SetCookie));
        }

        Assert.Equal(0, accountProbe.Count);
        Assert.Equal(0, tokenProbe.Count);
        Assert.Equal(messagesBefore, _factory.EmailSender.Messages.Count);

        HttpResponseMessage valid = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/login",
            JsonContent.Create(new { email = Email, password = Password }));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Contains(
            valid.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BodylessAuthRoutes_EmptyJsonBodiesProceedToSuccess(
        bool includeContentLength)
    {
        UserId userId = await _factory.SeedUserAsync(
            email: $"empty-json-{includeContentLength}@example.test",
            password: Password);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, userId);
        (string Route, HttpStatusCode Status)[] cases =
        [
            ("/api/v1/auth/email/verify/request", HttpStatusCode.Accepted),
            ("/api/v1/auth/reauth/email", HttpStatusCode.Accepted),
            ("/api/v1/auth/logout", HttpStatusCode.NoContent),
        ];

        foreach ((string route, HttpStatusCode status) in cases)
        {
            using HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
                client,
                route,
                new EmptyJsonContent(includeContentLength),
                cookie);
            Assert.True(
                response.StatusCode == status,
                $"{route} returned {(int)response.StatusCode} instead of {(int)status}.");
        }
    }

    private static InvalidCase JsonCase(string route, object body) =>
        new(route, () => JsonContent.Create(body));

    private static InvalidCase MalformedCase(string route) =>
        new(route, () => new StringContent("{", Encoding.UTF8, "application/json"));

    private static async Task<string> SignInAsync(HttpClient client, UserId userId)
    {
        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return response.Headers.GetValues(HeaderNames.SetCookie)
            .Single(value => value.StartsWith("pu.auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private sealed record InvalidCase(string Route, Func<HttpContent> Content);

    private sealed class EmptyJsonContent : HttpContent
    {
        private readonly bool _includeContentLength;

        internal EmptyJsonContent(bool includeContentLength)
        {
            _includeContentLength = includeContentLength;
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return _includeContentLength;
        }
    }

    private sealed class MutationProbe
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal void Record() => Interlocked.Increment(ref _count);

        internal void Reset() => Volatile.Write(ref _count, 0);
    }

    private sealed class ProbingTokenService(
        SecurityTokenService inner,
        MutationProbe probe) : ISecurityTokenService
    {
        public Task<IssuedToken> IssueAsync(
            IssueTokenRequest request,
            CancellationToken cancellationToken)
        {
            probe.Record();
            return inner.IssueAsync(request, cancellationToken);
        }

        public Task<TokenRedemption> RedeemAsync(
            RedeemTokenRequest request,
            CancellationToken cancellationToken)
        {
            probe.Record();
            return inner.RedeemAsync(request, cancellationToken);
        }
    }

    private sealed class ProbingAccountGateway(
        UserAccountGateway inner,
        MutationProbe probe) : IUserAccountGateway
    {
        public Task<UserId?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) =>
            inner.FindUserIdByEmailAsync(email, cancellationToken);

        public Task<UserId?> FindUserIdByLoginAsync(string provider, string providerKey, CancellationToken cancellationToken) =>
            inner.FindUserIdByLoginAsync(provider, providerKey, cancellationToken);

        public Task<PasswordCheckResult> CheckPasswordAsync(UserId id, string password, CancellationToken cancellationToken) =>
            inner.CheckPasswordAsync(id, password, cancellationToken);

        public Task VerifyDummyPasswordAsync(CancellationToken cancellationToken) =>
            inner.VerifyDummyPasswordAsync(cancellationToken);

        public Task<AccountEmailInfo?> GetEmailAsync(UserId id, CancellationToken cancellationToken) =>
            inner.GetEmailAsync(id, cancellationToken);

        public Task<IReadOnlyList<string>> ListLoginProvidersAsync(UserId id, CancellationToken cancellationToken) =>
            inner.ListLoginProvidersAsync(id, cancellationToken);

        public Task CreateAccountAsync(UserId id, string? email, bool emailConfirmed, string? password, CancellationToken cancellationToken) =>
            Mutate(() => inner.CreateAccountAsync(id, email, emailConfirmed, password, cancellationToken));

        public Task SetPasswordAsync(UserId id, string password, CancellationToken cancellationToken) =>
            Mutate(() => inner.SetPasswordAsync(id, password, cancellationToken));

        public Task RemovePasswordAsync(UserId id, CancellationToken cancellationToken) =>
            Mutate(() => inner.RemovePasswordAsync(id, cancellationToken));

        public Task SetEmailAsync(UserId id, string email, bool confirmed, CancellationToken cancellationToken) =>
            Mutate(() => inner.SetEmailAsync(id, email, confirmed, cancellationToken));

        public Task ConfirmEmailAsync(UserId id, CancellationToken cancellationToken) =>
            Mutate(() => inner.ConfirmEmailAsync(id, cancellationToken));

        public Task AddLoginAsync(UserId id, string provider, string providerKey, CancellationToken cancellationToken) =>
            Mutate(() => inner.AddLoginAsync(id, provider, providerKey, cancellationToken));

        public Task RemoveLoginAsync(UserId id, string provider, CancellationToken cancellationToken) =>
            Mutate(() => inner.RemoveLoginAsync(id, provider, cancellationToken));

        public Task PurgeCredentialsAsync(UserId id, CancellationToken cancellationToken) =>
            Mutate(() => inner.PurgeCredentialsAsync(id, cancellationToken));

        public Task EnsureRoleExistsAsync(string role, CancellationToken cancellationToken) =>
            Mutate(() => inner.EnsureRoleExistsAsync(role, cancellationToken));

        public Task<bool> AddToRoleAsync(UserId id, string role, CancellationToken cancellationToken)
        {
            probe.Record();
            return inner.AddToRoleAsync(id, role, cancellationToken);
        }

        private Task Mutate(Func<Task> mutation)
        {
            probe.Record();
            return mutation();
        }
    }
}
