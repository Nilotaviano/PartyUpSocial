using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Api.IntegrationTests.Sessions;
using PartyUp.Domain.Availability;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Availability;

/// <summary>
/// PHASE5_DESIGN.md A-079's byte budget: the documented worst-case fixture — 150 accepted friends,
/// each with 3 weekly series and 20 one-time blocks, over a 92-day range, plus 200 visible sessions for
/// <c>/calendar</c> — serialized, each response must stay under 4 MiB. A breach halts the slice
/// (BLOCKED), never a silent page/truncate.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AvailabilityPayloadBudgetTests : IAsyncLifetime, IDisposable
{
    private const long BudgetBytes = 4 * 1024 * 1024;
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["America/Fortaleza"];

    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task OverlapsMyAvailabilityAndCalendarStayUnderFourMebibytesOnTheWorstCaseFixture_A079()
    {
        UserId viewerId = await _factory.SeedUserAsync(email: $"budget-viewer-{Guid.NewGuid():N}@example.test");
        LocalDate anchor = _factory.Clock.GetCurrentInstant().InZone(Zone).Date.PlusDays(3);

        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
        User viewer = await context.DomainUsers.SingleAsync(u => u.Id == viewerId);

        // The viewer's own availability layer, at the same one-owner shape every friend below gets.
        AddOwnerAvailability(context, viewerId, anchor);

        // 150 accepted friends, each with 3 weekly series and 20 one-time blocks.
        for (int i = 0; i < 150; i++)
        {
            User friend = BuildActiveUser(i);
            context.DomainUsers.Add(friend);
            Friendship friendship = Friendship.Request(viewer, friend, _factory.Clock);
            friendship.Accept(friend.Id, _factory.Clock);
            _ = friendship.DequeueEvents();
            context.Friendships.Add(friendship);
            AddOwnerAvailability(context, friend.Id, anchor);
        }

        // +200 visible sessions for /calendar, spread across the range, organized by the viewer.
        for (int i = 0; i < 200; i++)
        {
            Instant start = anchor.AtStartOfDayInZone(Zone).ToInstant() + Duration.FromHours(6 * i % (92 * 24));
            SessionDetails details = new(
                SessionTitle.Create($"Budget fixture session {i}"),
                start,
                Duration15.FromMinutes(60),
                Platform.NotApplicable,
                null,
                SessionNotes.Empty,
                minAttendance: 1,
                maxCapacity: null,
                visibility: (true ? PartyUp.Domain.Kernel.ContentVisibility.Private : PartyUp.Domain.Kernel.ContentVisibility.Public));
            Session session = Session.Create(viewerId, details, [], _factory.Clock);
            _ = session.DequeueEvents();
            context.Sessions.Add(session);
        }

        await context.SaveChangesAsync();

        using SessionHttpSession viewerSession = await SessionEndpointTestSupport.SignInAsync(_factory, viewerId);
        Instant from = anchor.AtStartOfDayInZone(Zone).ToInstant();
        Instant to = from + Duration.FromDays(92);
        string fromText = NodaTime.Text.InstantPattern.ExtendedIso.Format(from);
        string toText = NodaTime.Text.InstantPattern.ExtendedIso.Format(to);

        long overlapsBytes = await MeasureAsync(
            viewerSession, $"/api/v1/availability/overlaps?from={fromText}&to={toText}");
        long myAvailabilityBytes = await MeasureAsync(
            viewerSession, $"/api/v1/me/availability?from={fromText}&to={toText}");
        long calendarBytes = await MeasureAsync(
            viewerSession, $"/api/v1/calendar?from={fromText}&to={toText}");

        Console.WriteLine(
            $"MEASURED BYTES overlaps={overlapsBytes} myAvailability={myAvailabilityBytes} calendar={calendarBytes}");

        // A-079 escalation: a breach halts the slice (never silently paged/truncated) — the assertion
        // below is that escalation trigger, and the measured figures are recorded in TRACEABILITY.md.
        Assert.True(
            overlapsBytes <= BudgetBytes,
            $"GET /availability/overlaps was {overlapsBytes} bytes, over the {BudgetBytes} budget.");
        Assert.True(
            myAvailabilityBytes <= BudgetBytes,
            $"GET /me/availability was {myAvailabilityBytes} bytes, over the {BudgetBytes} budget.");
        Assert.True(
            calendarBytes <= BudgetBytes,
            $"GET /calendar was {calendarBytes} bytes, over the {BudgetBytes} budget.");
    }

    private static void AddOwnerAvailability(AppDbContext context, UserId ownerId, LocalDate anchor)
    {
        IsoDayOfWeek[] weekdays = [IsoDayOfWeek.Monday, IsoDayOfWeek.Wednesday, IsoDayOfWeek.Friday];
        foreach (IsoDayOfWeek weekday in weekdays)
        {
            AvailabilitySeries series = AvailabilitySeries.Create(
                ownerId,
                WeeklyPattern.Create(weekday, new LocalTime(18, 0), Duration15.FromMinutes(120)),
                Zone,
                anchor);
            context.AvailabilitySeries.Add(series);
        }

        for (int i = 0; i < 20; i++)
        {
            Instant start = anchor.AtStartOfDayInZone(Zone).ToInstant()
                + Duration.FromHours(4 * i)
                + Duration.FromDays(i % 90);
            AvailabilityBlock block = AvailabilityBlock.Create(
                ownerId, new TimeRange(start, start + Duration.FromMinutes(90)));
            context.AvailabilityBlocks.Add(block);
        }
    }

    private static User BuildActiveUser(int index)
    {
        string token = Guid.NewGuid().ToString("N");
        User user = User.BeginOnboarding(
            UserId.New(), FriendCode.Create($"F{token}"[..10]), AgeGateSource.SelfAttested, SystemClock.Instance);
        user.CompleteOnboarding(
            Username.Create($"u{token}"[..20]),
            DisplayName.Create($"Budget Friend {index}"),
            Avatar.FromEmoji("🎮"),
            Zone,
            Locale.Create("en"),
            SystemClock.Instance);
        return user;
    }

    private static async Task<long> MeasureAsync(SessionHttpSession session, string path)
    {
        HttpResponseMessage response = await session.GetAsync(path);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return body.LongLength;
    }
}
