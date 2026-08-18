using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using PartyUp.Api.Contracts.Feedback;
using PartyUp.Api.Endpoints;
using PartyUp.Application.Email;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Feedback;

/// <summary>
/// D-212..D-216 over the wire: the account-less submit route, its dedicated rate-limit dimensions,
/// the admin-only read/toggle surface, the dashboard's live unhandled count, and the account-deletion
/// anonymization pass.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FeedbackRouteTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Submit_FromAnAnonymousVisitor_IsAcceptedWithNoSubmitter_D212_D213()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage response = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("idea", "A guest can reach this.", "/guest/abc"),
            forwardedFor: "198.51.100.10");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SubmitFeedbackResponse body =
            (await response.Content.ReadFromJsonAsync<SubmitFeedbackResponse>())!;

        FeedbackItem stored = await ReadAsync(body.Id);
        Assert.Null(stored.SubmitterUserId);
        Assert.Equal(FeedbackType.Idea, stored.Type);
        Assert.Equal("A guest can reach this.", stored.Message);
        Assert.Equal("/guest/abc", stored.Route);
        Assert.Null(stored.UserAgent);
        Assert.Null(stored.Locale);
        Assert.Null(stored.AppVersion);
        Assert.False(stored.Handled);
        Assert.Null(stored.HandledAt);
    }

    [Fact]
    public async Task Submit_FromASignedInUser_RecordsTheSubmitter_D213()
    {
        UserId submitter = await _factory.SeedUserAsync(email: "feedback-author@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, submitter);

        HttpResponseMessage response = await SubmitAsync(
            client,
            new SubmitFeedbackRequest(
                "bug",
                "The week grid drops my taps.",
                "/polls/1",
                "Mozilla/5.0 (Test)",
                "pt-BR",
                "1.4.2"),
            forwardedFor: "198.51.100.11",
            cookie: cookie);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        SubmitFeedbackResponse body =
            (await response.Content.ReadFromJsonAsync<SubmitFeedbackResponse>())!;

        FeedbackItem stored = await ReadAsync(body.Id);
        Assert.Equal(submitter, stored.SubmitterUserId);
        Assert.Equal(FeedbackType.Bug, stored.Type);
        Assert.Equal("Mozilla/5.0 (Test)", stored.UserAgent);
        Assert.Equal("pt-BR", stored.Locale);
        Assert.Equal("1.4.2", stored.AppVersion);
    }

    [Fact]
    public async Task Submit_WithAnEmptyMessageOrUnknownType_Is400_D212()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);

        HttpResponseMessage emptyMessage = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("other", "   "),
            forwardedFor: "198.51.100.12");
        Assert.Equal(HttpStatusCode.BadRequest, emptyMessage.StatusCode);

        HttpResponseMessage unknownType = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("praise", "Not a listed type."),
            forwardedFor: "198.51.100.12");
        Assert.Equal(HttpStatusCode.BadRequest, unknownType.StatusCode);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await context.FeedbackItems.CountAsync());
    }

    /// <summary>
    /// D-213: five per fifteen minutes per IP. The sixth attempt from the same address is a 429 even
    /// though nothing else about the request changed.
    /// </summary>
    [Fact]
    public async Task Submit_BeyondFivePerFifteenMinutesFromOneAddress_Is429_D213()
    {
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        const string address = "198.51.100.20";

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            HttpResponseMessage accepted = await SubmitAsync(
                client,
                new SubmitFeedbackRequest("other", $"Submission {attempt}."),
                forwardedFor: address);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        HttpResponseMessage limited = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("other", "Submission 6."),
            forwardedFor: address);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // The dimension is per-IP: a different address still has its own budget.
        HttpResponseMessage elsewhere = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("other", "From another address."),
            forwardedFor: "198.51.100.21");
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    /// <summary>
    /// D-213: an authenticated caller is bound by the user dimension too, so moving to a fresh IP
    /// does not buy them a sixth submission.
    /// </summary>
    [Fact]
    public async Task Submit_BeyondFivePerFifteenMinutesForOneUser_Is429AcrossAddresses_D213()
    {
        UserId submitter = await _factory.SeedUserAsync(email: "feedback-limited@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, submitter);

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            HttpResponseMessage accepted = await SubmitAsync(
                client,
                new SubmitFeedbackRequest("bug", $"Submission {attempt}."),
                forwardedFor: $"198.51.100.{100 + attempt}",
                cookie: cookie);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        HttpResponseMessage limited = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("bug", "Submission 6 from a fresh address."),
            forwardedFor: "198.51.100.200",
            cookie: cookie);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Submit_MailsEveryConfiguredAdminSeedAddressAfterTheRowCommits_D215()
    {
        await using WebApplicationFactory<Program> configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Admin:SeedEmails:0"] = "feedback-admin-one@example.test",
                    ["Admin:SeedEmails:1"] = "feedback-admin-two@example.test",
                })));
        using HttpClient client = Slice5HttpTest.CreateClient(configured);

        HttpResponseMessage response = await SubmitAsync(
            client,
            new SubmitFeedbackRequest("bug", "Mail this to the admins."),
            forwardedFor: "198.51.100.30");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        IReadOnlyList<EmailMessage> mails = await _factory.EmailSender.WaitForCountAsync(
            message => message.Template == EmailTemplate.AdminFeedbackSubmitted,
            2);

        Assert.Equal(
            ["feedback-admin-one@example.test", "feedback-admin-two@example.test"],
            mails.Select(message => message.ToAddress).Order(StringComparer.Ordinal).ToArray());
        Assert.All(mails, message => Assert.Equal("Bug", message.Model["Type"]));
        Assert.All(mails, message => Assert.Equal("anonymous", message.Model["Submitter"]));
    }

    [Fact]
    public async Task AdminFeedback_ForANonAdmin_Is404_D214()
    {
        UserId member = await _factory.SeedUserAsync(email: "feedback-nonadmin@example.test");
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, member);
        Guid id = await SeedFeedbackAsync(FeedbackType.Bug, "Admin only.", handled: false);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/feedback");
        listRequest.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage list = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);

        HttpResponseMessage toggle = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/feedback/{id:D}/handled",
            JsonContent.Create(new SetFeedbackHandledRequest(true)),
            cookie);
        Assert.Equal(HttpStatusCode.NotFound, toggle.StatusCode);

        // Fail closed means the row is untouched, not merely unreadable.
        Assert.False((await ReadAsync(id)).Handled);
    }

    [Fact]
    public async Task AdminFeedback_ListsNewestFirstAndFiltersByTypeAndHandled_D214()
    {
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "feedback-list-admin@example.test");
        Guid oldestBug = await SeedFeedbackAsync(FeedbackType.Bug, "Oldest bug.", handled: true);
        _factory.Clock.Advance(NodaTime.Duration.FromMinutes(5));
        Guid idea = await SeedFeedbackAsync(FeedbackType.Idea, "An idea.", handled: false);
        _factory.Clock.Advance(NodaTime.Duration.FromMinutes(5));
        Guid newestBug = await SeedFeedbackAsync(FeedbackType.Bug, "Newest bug.", handled: false);

        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);

        AdminFeedbackListResponse all = await ListAsync(client, cookie, string.Empty);
        Assert.Equal(3, all.Total);
        Assert.Equal(
            [newestBug, idea, oldestBug],
            all.Items.Select(item => item.Id).ToArray());

        AdminFeedbackListResponse bugs = await ListAsync(client, cookie, "?type=bug");
        Assert.Equal([newestBug, oldestBug], bugs.Items.Select(item => item.Id).ToArray());

        AdminFeedbackListResponse unhandled = await ListAsync(client, cookie, "?handled=false");
        Assert.Equal([newestBug, idea], unhandled.Items.Select(item => item.Id).ToArray());

        AdminFeedbackListResponse unhandledBugs =
            await ListAsync(client, cookie, "?type=bug&handled=false");
        Assert.Equal([newestBug], unhandledBugs.Items.Select(item => item.Id).ToArray());

        using var badFilter = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/admin/feedback?type=praise");
        badFilter.Headers.Add(HeaderNames.Cookie, cookie);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(badFilter)).StatusCode);
    }

    [Fact]
    public async Task AdminFeedback_TogglesHandledAndStampsHandledAt_D214()
    {
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "feedback-toggle-admin@example.test");
        Guid id = await SeedFeedbackAsync(FeedbackType.Other, "Toggle me.", handled: false);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);

        HttpResponseMessage handled = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/feedback/{id:D}/handled",
            JsonContent.Create(new SetFeedbackHandledRequest(true)),
            cookie);
        Assert.Equal(HttpStatusCode.OK, handled.StatusCode);
        AdminFeedbackResponse marked =
            (await handled.Content.ReadFromJsonAsync<AdminFeedbackResponse>())!;
        Assert.True(marked.Handled);
        Assert.NotNull(marked.HandledAt);
        Assert.True((await ReadAsync(id)).Handled);

        HttpResponseMessage cleared = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/feedback/{id:D}/handled",
            JsonContent.Create(new SetFeedbackHandledRequest(false)),
            cookie);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        AdminFeedbackResponse unmarked =
            (await cleared.Content.ReadFromJsonAsync<AdminFeedbackResponse>())!;
        Assert.False(unmarked.Handled);
        Assert.Null(unmarked.HandledAt);
        Assert.Null((await ReadAsync(id)).HandledAt);

        HttpResponseMessage missing = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/feedback/{Guid.CreateVersion7():D}/handled",
            JsonContent.Create(new SetFeedbackHandledRequest(true)),
            cookie);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Dashboard_ReportsTheLiveUnhandledFeedbackCount_D214()
    {
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "feedback-dashboard-admin@example.test");
        await SeedFeedbackAsync(FeedbackType.Bug, "Unhandled one.", handled: false);
        await SeedFeedbackAsync(FeedbackType.Idea, "Unhandled two.", handled: false);
        Guid done = await SeedFeedbackAsync(FeedbackType.Other, "Already handled.", handled: true);

        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);

        Assert.Equal(2, (await ReadDashboardAsync(client, cookie)).UnhandledFeedback);

        HttpResponseMessage reopened = await AntiforgeryTestClient.PostAsync(
            client,
            $"/api/v1/admin/feedback/{done:D}/handled",
            JsonContent.Create(new SetFeedbackHandledRequest(false)),
            cookie);
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);

        Assert.Equal(3, (await ReadDashboardAsync(client, cookie)).UnhandledFeedback);
    }

    [Fact]
    public async Task DeletionExecution_NullsTheSubmitterAndRetainsTheRow_D216()
    {
        UserId leaving = await _factory.SeedUserAsync(
            pendingDeletion: true,
            email: "feedback-leaving@example.test");
        UserId staying = await _factory.SeedUserAsync(email: "feedback-staying@example.test");
        UserId admin = await _factory.SeedUserAsync(
            admin: true,
            email: "feedback-deletion-admin@example.test");

        Guid theirs = await SeedFeedbackAsync(
            FeedbackType.Bug,
            "Retained after deletion.",
            handled: false,
            submitter: leaving);
        Guid someoneElses = await SeedFeedbackAsync(
            FeedbackType.Idea,
            "Somebody else's row.",
            handled: false,
            submitter: staying);

        _factory.Clock.Advance(User.DeletionGracePeriod);
        using HttpClient client = Slice5HttpTest.CreateClient(_factory);
        string cookie = await SignInAsync(client, admin);

        HttpResponseMessage sweep = await AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/admin/maintenance/deletion-sweep",
            content: null,
            cookie);
        Assert.Equal(HttpStatusCode.OK, sweep.StatusCode);

        FeedbackItem anonymized = await ReadAsync(theirs);
        Assert.Null(anonymized.SubmitterUserId);
        Assert.Equal("Retained after deletion.", anonymized.Message);
        Assert.Equal(FeedbackType.Bug, anonymized.Type);

        FeedbackItem untouched = await ReadAsync(someoneElses);
        Assert.Equal(staying, untouched.SubmitterUserId);
    }

    private static Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        SubmitFeedbackRequest request,
        string forwardedFor,
        string? cookie = null) =>
        AntiforgeryTestClient.PostAsync(
            client,
            "/api/v1/feedback",
            JsonContent.Create(request),
            cookie,
            forwardedFor);

    private static async Task<AdminFeedbackListResponse> ListAsync(
        HttpClient client,
        string cookie,
        string query)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/admin/feedback{query}");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminFeedbackListResponse>())!;
    }

    private static async Task<AdminDashboardResponse> ReadDashboardAsync(
        HttpClient client,
        string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/dashboard");
        request.Headers.Add(HeaderNames.Cookie, cookie);
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdminDashboardResponse>())!;
    }

    private async Task<Guid> SeedFeedbackAsync(
        FeedbackType type,
        string message,
        bool handled,
        UserId? submitter = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        FeedbackItem item = FeedbackItem.Create(
            submitter,
            type,
            message,
            "/seeded",
            null,
            "en",
            "1.0.0",
            _factory.Clock);
        if (handled)
        {
            item.SetHandled(true, _factory.Clock);
        }

        context.FeedbackItems.Add(item);
        await context.SaveChangesAsync();
        return item.Id.Value;
    }

    private async Task<FeedbackItem> ReadAsync(Guid id)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.FeedbackItems
            .AsNoTracking()
            .SingleAsync(item => item.Id == FeedbackItemId.From(id));
    }

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
}
