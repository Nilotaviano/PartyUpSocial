using System.Net;
using System.Net.Http.Json;
using NodaTime;
using PartyUp.Api.Security;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// A-073's registered-side dimensions (PHASE4_DESIGN.md §8, §3): <c>session-write</c> and
/// <c>session-attach</c> partition by the authenticated user, not globally, so one organizer's burst
/// never throttles another.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionRateLimitTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void DimensionsAreConfiguredAsDesigned()
    {
        RateLimitDimension sessionWrite = RateLimitPolicies.Dimensions.Single(d => d.Name == "session-write");
        Assert.Equal(30, sessionWrite.PermitLimit);
        Assert.Equal(RateLimitPartitionKind.User, sessionWrite.PartitionKind);
        Assert.Equal(Duration.FromMinutes(1).ToTimeSpan(), sessionWrite.Window);

        RateLimitDimension sessionAttach = RateLimitPolicies.Dimensions.Single(d => d.Name == "session-attach");
        Assert.Equal(60, sessionAttach.PermitLimit);
        Assert.Equal(RateLimitPartitionKind.User, sessionAttach.PartitionKind);
    }

    [Fact]
    public async Task SessionWriteAndAttachDimensionsPartitionByUser_A073()
    {
        SessionHttpUser first = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser second = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession firstSession = await SessionEndpointTestSupport.SignInAsync(_factory, first.Id);
        using SessionHttpSession secondSession = await SessionEndpointTestSupport.SignInAsync(_factory, second.Id);

        RateLimitDimension sessionWrite = RateLimitPolicies.Dimensions.Single(d => d.Name == "session-write");

        HttpStatusCode? rejectedStatus = null;
        for (int i = 0; i < sessionWrite.PermitLimit + 1; i++)
        {
            HttpResponseMessage response = await firstSession.SendUnsafeAsync(
                HttpMethod.Post,
                "/api/v1/sessions",
                JsonContent.Create(new
                {
                    title = $"Rate limit {i}",
                    startTime = SessionEndpointTestSupport.Iso(
                        _factory.Clock.GetCurrentInstant() + Duration.FromDays(1) + Duration.FromMinutes(i)),
                    isPrivate = true,
                }));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejectedStatus = response.StatusCode;
                break;
            }

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedStatus);

        // A different user's own bucket is untouched — the dimension partitions per user, not globally.
        HttpResponseMessage secondUserResponse = await secondSession.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Second user still has budget",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + Duration.FromDays(1)),
                isPrivate = true,
            }));
        Assert.Equal(HttpStatusCode.Created, secondUserResponse.StatusCode);
    }
}
