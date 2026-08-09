using System.Net;
using System.Net.Http.Json;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Email;

namespace PartyUp.Api.IntegrationTests;

/// <summary>
/// ticket-HOTFIX-auth500: every auth route whose use case can throw
/// <c>DomainError.ValidationFailed</c> (via <c>AccountInput.RequirePassword</c>/<c>AccountInput.Email</c>)
/// must map to 400 <c>auth_validation</c> instead of leaking a 500 through
/// <see cref="PartyUp.Api.ErrorHandling.DomainExceptionMapper"/>'s generic fallback.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuthValidationProblemTests : IAsyncLifetime, IDisposable
{
    private const string ValidPassword = "correct-password-12";
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task VerifyEmail_ShortPassword_Is400AuthValidation()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string email = $"verify-short-pw-{Guid.NewGuid():N}@example.test";
        HttpResponseMessage registration = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new { email, ageGateAttested = true, locale = "en" }));
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

        EmailMessage verification = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == email &&
                message.Template == EmailTemplate.EmailVerification);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new { token = verification.Model["Token"], password = "short-pw" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("auth_validation", problem.Code);
        Assert.Contains("12", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangePassword_ShortPassword_Is400AuthValidation()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        PartyUp.Domain.Kernel.UserId userId = await _factory.SeedUserAsync(
            email: $"change-short-pw-{Guid.NewGuid():N}@example.test",
            password: ValidPassword);
        string cookie = await SignInAsync(client, userId);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/change",
            JsonContent.Create(new { currentPassword = ValidPassword, newPassword = "short-pw" }),
            cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("auth_validation", problem.Code);
        Assert.Contains("12", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetPassword_ShortPassword_Is400AuthValidation()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/password/reset",
            JsonContent.Create(new { token = "irrelevant-token", newPassword = "short-pw" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AuthProblem problem = (await response.Content.ReadFromJsonAsync<AuthProblem>())!;
        Assert.Equal("auth_validation", problem.Code);
        Assert.Contains("12", problem.Detail, StringComparison.Ordinal);
    }

    /// <summary>Regression arm: an over-broad catch must not swallow the legitimate happy path.</summary>
    [Fact]
    public async Task VerifyEmail_ValidPassword_StillSucceeds()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string email = $"verify-valid-pw-{Guid.NewGuid():N}@example.test";
        HttpResponseMessage registration = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            JsonContent.Create(new { email, ageGateAttested = true, locale = "en" }));
        Assert.Equal(HttpStatusCode.Accepted, registration.StatusCode);

        EmailMessage verification = await _factory.EmailSender.WaitForAsync(
            message => message.ToAddress == email &&
                message.Template == EmailTemplate.EmailVerification);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/email/verify",
            JsonContent.Create(new { token = verification.Model["Token"], password = ValidPassword }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues(HeaderNames.SetCookie),
            value => value.StartsWith("pu.auth=", StringComparison.Ordinal));
    }

    private static async Task<string> SignInAsync(HttpClient client, PartyUp.Domain.Kernel.UserId userId)
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
}
