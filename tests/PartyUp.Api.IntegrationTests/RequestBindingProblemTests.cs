using System.Net;
using System.Text;
using System.Text.Json;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class RequestBindingProblemTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ExistingPhase2BodyRoute_MalformedJsonUsesUniformShapeOnly()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/auth/register",
            new StringContent("{", Encoding.UTF8, "application/json"));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "validation_failed",
            "/api/v1/auth/register");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/xml")]
    public async Task FriendBodyRoute_MissingOrUnsupportedContentTypeUsesUniformShape(
        string? contentType)
    {
        string suffix = Guid.NewGuid().ToString("N");
        PartyUp.Domain.Kernel.UserId userId = await _factory.SeedUserAsync(
            email: $"binding-{suffix}@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        HttpResponseMessage signIn = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/_slice2/sign-in/{userId.Value:D}",
            content: null);
        string cookie = Slice5HttpTest.AuthCookie(signIn);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
        if (contentType is not null)
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        HttpResponseMessage response = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/friend-requests",
            content,
            cookie);

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type",
            "/api/v1/friend-requests");
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string instance)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.Equal((int)status, root.GetProperty("status").GetInt32());
        Assert.Equal(instance, root.GetProperty("instance").GetString());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.False(root.TryGetProperty("detail", out _));
    }
}
