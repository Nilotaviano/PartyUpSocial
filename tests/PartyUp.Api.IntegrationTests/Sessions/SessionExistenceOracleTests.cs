using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using PartyUp.Domain.Sessions;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// A-055 on the session write surface: a session the visibility engine denies must answer exactly the
/// same 404 <c>session_not_found</c> a never-created session answers, so neither the reminder write nor
/// the RSVP write can be used as an existence or lifecycle-state oracle.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionExistenceOracleTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task NonMemberReminderWriteOnAnExistingPrivateSessionIsIndistinguishableFromAMissingOne_A055()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        Session session = await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id, isPrivate: true);

        using SessionHttpSession strangerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);

        HttpResponseMessage existing = await strangerSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{session.Id.Value:D}/my-reminder",
            JsonContent.Create(new { choice = "Hours1" }));
        Guid absentId = Guid.NewGuid();
        HttpResponseMessage absent = await strangerSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{absentId:D}/my-reminder",
            JsonContent.Create(new { choice = "Hours1" }));

        await SessionEndpointTestSupport.AssertProblemAsync(existing, HttpStatusCode.NotFound, "session_not_found");
        await SessionEndpointTestSupport.AssertProblemAsync(absent, HttpStatusCode.NotFound, "session_not_found");

        // Byte-compare the two bodies with only the request-correlated fields normalized: the instance
        // path carries the id that was asked for, and traceId is per-request by construction.
        Assert.Equal(
            await NormalizedBodyAsync(existing, session.Id.Value),
            await NormalizedBodyAsync(absent, absentId));
    }

    [Fact]
    public async Task NonMemberRsvpOnACompletedSessionIsNotFoundNotATerminalStateAnswer_A055()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser invited = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser friend = await SessionEndpointTestSupport.SeedAsync(_factory);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, invited.Id);
        await SessionEndpointTestSupport.MakeFriendsAsync(_factory, organizer.Id, friend.Id);

        // Public + friend-of-organizer, so the visibility engine grants the prober inherited access:
        // the only guard left is the roster check, which must answer before the terminal-state guard
        // does. (D-099 requires the public session to carry one invited registered friend.)
        Session session = await SessionEndpointTestSupport.SeedSessionAsync(
            _factory,
            organizer.Id,
            leadMinutes: 20,
            durationMinutes: 15,
            isPrivate: false,
            invitees: [invited.Id]);

        _factory.Clock.Advance(Duration.FromMinutes(40));
        await SessionEndpointTestSupport.RunDueSweepAsync(_factory);

        using SessionHttpSession friendSession = await SessionEndpointTestSupport.SignInAsync(_factory, friend.Id);
        HttpResponseMessage response = await friendSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{session.Id.Value:D}/my-participation",
            JsonContent.Create(new { response = "Going" }));

        await SessionEndpointTestSupport.AssertProblemAsync(response, HttpStatusCode.NotFound, "session_not_found");
    }

    [Fact]
    public async Task NonMemberRsvpOnAnExistingPrivateSessionIsIndistinguishableFromAMissingOne_A055()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        SessionHttpUser stranger = await SessionEndpointTestSupport.SeedAsync(_factory);
        Session session = await SessionEndpointTestSupport.SeedSessionAsync(_factory, organizer.Id, isPrivate: true);

        using SessionHttpSession strangerSession =
            await SessionEndpointTestSupport.SignInAsync(_factory, stranger.Id);

        HttpResponseMessage existing = await strangerSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{session.Id.Value:D}/my-participation",
            JsonContent.Create(new { response = "Going" }));
        Guid absentId = Guid.NewGuid();
        HttpResponseMessage absent = await strangerSession.SendUnsafeAsync(
            HttpMethod.Put,
            $"/api/v1/sessions/{absentId:D}/my-participation",
            JsonContent.Create(new { response = "Going" }));

        await SessionEndpointTestSupport.AssertProblemAsync(existing, HttpStatusCode.NotFound, "session_not_found");
        Assert.Equal(
            await NormalizedBodyAsync(existing, session.Id.Value),
            await NormalizedBodyAsync(absent, absentId));
    }

    private static async Task<string> NormalizedBodyAsync(HttpResponseMessage response, Guid sessionId)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        string traceId = document.RootElement.GetProperty("traceId").GetString()!;
        return body
            .Replace(sessionId.ToString("D"), "{sessionId}", StringComparison.Ordinal)
            .Replace(traceId, "{traceId}", StringComparison.Ordinal);
    }
}
