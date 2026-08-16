using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.Security;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// A-073/D-150's guest-side rate limiting: exchange leases are target-independent (a forged token
/// costs the same as a real one), and the <c>guest-write</c> dimension falls back to the caller's IP
/// when the route carries no parseable <c>guestLinkId</c> — unreachable through the shipped
/// guid-constrained routes, so exercised directly against the partition function (see the
/// <c>InternalsVisibleTo</c> grant in <c>RateLimitPolicies.cs</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class GuestRateLimitTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TokenStatesConsumeEquivalentTargetIndependentLeases_D150()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: $"guest-rl-{Guid.NewGuid():N}@example.test");
        SessionDetails details = new(
            SessionTitle.Create("Rate limit session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromDays(1),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Private);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        (GuestLink _, string validToken) = session.CreateGuestLink(_factory.Clock);
        _ = session.DequeueEvents();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();

        using HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });

        RateLimitDimension dimension = RateLimitPolicies.Dimensions.Single(d => d.Name == "guest-link-exchange");
        HttpStatusCode? rejected = null;
        for (int i = 0; i < dimension.PermitLimit + 1; i++)
        {
            // Alternate between the real token and forged ones — a target-independent lease means the
            // shared bucket exhausts on total count, not on any one token's identity.
            string token = i % 2 == 0 ? validToken : Guid.NewGuid().ToString("N");
            HttpResponseMessage response = await client.GetAsync($"/g/{token}");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = response.StatusCode;
                break;
            }

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected);
    }

    [Fact]
    public void MissingOrMalformedLinkRouteValueFallsBackToTheIpPartition_A073()
    {
        RateLimitDimension guestWrite = RateLimitPolicies.Dimensions.Single(d => d.Name == "guest-write");
        byte[] pepper = "test-pepper"u8.ToArray();

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new EndpointNameMetadata(PartyUp.Api.ApiOperationIds.RespondAsGuest)),
            "guest-write-test"));
        // No "guestLinkId" route value is set at all — the absent-route-value branch.

        RateLimitPartition<string> partition = RateLimitPolicies.PartitionFor(context, guestWrite, pepper);
        string expectedKey = RateLimitPolicies.HashPartitionKey("guest-write", "203.0.113.7", pepper);

        Assert.Equal(expectedKey, partition.PartitionKey);
    }
}
