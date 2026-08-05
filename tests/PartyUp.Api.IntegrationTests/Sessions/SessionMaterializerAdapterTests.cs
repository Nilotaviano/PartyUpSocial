using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;
using PartyUp.Infrastructure.Persistence.Repositories;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// S2c shipped <see cref="SessionNotificationMaterializer"/> and <see cref="SessionReminderHandoffStore"/>
/// as the real adapters over the pure domain contracts (<c>RecipientComputer</c>, <c>ReminderRules</c>)
/// that S2c's Application-layer tests already proved against fakes. This file is the real-Postgres
/// round trip those two adapters were missing: the mute query shape actually suppresses a row at write
/// time, a block-derived event actually persists with no actor column, and the handoff store's
/// upsert/idempotent-cancel/Delivered-reset rules actually survive a round trip through real rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionMaterializerAdapterTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SessionNotificationMaterializer_MuteQueryShapeSuppressesRowsAtWriteTime_D090_D113()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "materializer-organizer@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: "materializer-participant@example.test");

        // Min = 2 so the organizer alone does not confirm the session: the participant's own Going
        // response is what flips it, which is what makes SessionUnconfirmed reachable below.
        Session session = CreateSession(organizerId, minAttendance: 2);
        session.Invite(participantId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(participantId), ParticipationStatus.Going, _factory.Clock);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Sessions.Add(session);
            context.NotificationMutes.Add(
                NotificationMute.ForSession(participantId, session.Id, _factory.Clock));
            await context.SaveChangesAsync();
        }

        // The participant leaving drops ActiveGoingCount back below the min, unconfirming the session
        // — a plain, non-block state-change broadcast that both the organizer and the (muted)
        // participant are otherwise eligible to receive (D-090/D-113: mute suppresses the row itself).
        session.Leave(ParticipantRef.ForUser(participantId), _factory.Clock);
        Assert.Equal(SessionStatus.Unconfirmed, session.Status);
        IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
        Assert.Contains(events, e => e is SessionUnconfirmed);

        await using (AsyncServiceScope materializeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = materializeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var materializer = new SessionNotificationMaterializer(context, _factory.Clock);
            await materializer.MaterializeAsync(session, events, default);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        List<Notification> notifications = await verify.Notifications
            .AsNoTracking()
            .Where(n => n.SessionId == session.Id && n.Type == NotificationType.SessionUnconfirmed)
            .ToListAsync();
        Assert.Contains(notifications, n => n.RecipientUserId == organizerId);
        Assert.DoesNotContain(notifications, n => n.RecipientUserId == participantId);
    }

    [Fact]
    public async Task SessionNotificationMaterializer_BlockDerivedRemoval_WritesNoActorOrCauseColumn_D105()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "block-derived-organizer@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: "block-derived-participant@example.test");

        Session session = CreateSession(organizerId, minAttendance: 2);
        session.Invite(participantId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(participantId), ParticipationStatus.Going, _factory.Clock);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        // This is exactly what BlockEffects.Apply does to a counterpart's row (Friends/BlockEffects.cs):
        // RemoveParticipation, never a domain event that names the blocker or the block direction.
        session.RemoveParticipation(ParticipantRef.ForUser(participantId), _factory.Clock);
        Assert.Equal(SessionStatus.Unconfirmed, session.Status);
        IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
        Assert.Contains(events, e => e is SessionUnconfirmed);

        await using (AsyncServiceScope materializeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = materializeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var materializer = new SessionNotificationMaterializer(context, _factory.Clock);
            await materializer.MaterializeAsync(session, events, default);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Notification organizerRow = await verify.Notifications.AsNoTracking().SingleAsync(n =>
            n.SessionId == session.Id &&
            n.Type == NotificationType.SessionUnconfirmed &&
            n.RecipientUserId == organizerId);
        Assert.Null(organizerRow.ActorUserId);
    }

    /// <summary>
    /// F4/DOMAIN_MODEL.md §11 step 5, real adapter round trip: the excluded user gets no row for a
    /// batch of events even though they would otherwise be an eligible recipient (the organizer, here,
    /// for the SessionUnconfirmed broadcast) — the deletion executor's hook actually suppresses at
    /// write time, not just in the pure <c>RecipientFilter</c> contract already proven in
    /// <c>SessionNotificationMaterializerTests</c>.
    /// </summary>
    [Fact]
    public async Task SessionNotificationMaterializer_ExcludedUserReceivesNoRowEvenIfOtherwiseEligible_D105()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "materializer-exclude-organizer@example.test");
        UserId participantId = await _factory.SeedUserAsync(email: "materializer-exclude-participant@example.test");

        Session session = CreateSession(organizerId, minAttendance: 2);
        session.Invite(participantId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(participantId), ParticipationStatus.Going, _factory.Clock);
        Assert.Equal(SessionStatus.Confirmed, session.Status);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        session.RemoveParticipation(ParticipantRef.ForUser(participantId), _factory.Clock);
        Assert.Equal(SessionStatus.Unconfirmed, session.Status);
        IReadOnlyList<IDomainEvent> events = session.DequeueEvents();
        Assert.Contains(events, e => e is SessionUnconfirmed);

        await using (AsyncServiceScope materializeScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = materializeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var materializer = new SessionNotificationMaterializer(context, _factory.Clock);
            await materializer.MaterializeAsync(session, events, organizerId, default);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        List<Notification> notifications = await verify.Notifications
            .AsNoTracking()
            .Where(n => n.SessionId == session.Id && n.Type == NotificationType.SessionUnconfirmed)
            .ToListAsync();
        Assert.DoesNotContain(notifications, n => n.RecipientUserId == organizerId);
    }

    [Fact]
    public async Task SessionReminderHandoffStore_UpsertsAndIncrementsVersionAcrossReplans_D082()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "handoff-organizer@example.test");
        UserId recipientId = await _factory.SeedUserAsync(email: "handoff-recipient@example.test");
        Session session = CreateSession(organizerId, minAttendance: 1);
        session.Invite(recipientId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(recipientId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope firstScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var writer = new SessionReminderHandoffStore(context, _factory.Clock);
            await writer.ReplanAsync(session, recipientId, default);
            await context.SaveChangesAsync();
        }

        SessionReminderHandoff afterFirstPlan;
        await using (AsyncServiceScope readScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            afterFirstPlan = await context.SessionReminderHandoffs.AsNoTracking().SingleAsync(h =>
                h.SessionId == session.Id && h.RecipientUserId == recipientId);
        }

        Assert.Equal(SessionReminderHandoffState.Pending, afterFirstPlan.State);
        Assert.Equal(0, afterFirstPlan.Version);

        // A second recompute trigger over the same eligible row (e.g. a reminder-choice change) must
        // upsert the existing row rather than duplicate it, and increment its version.
        await using (AsyncServiceScope secondScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var writer = new SessionReminderHandoffStore(context, _factory.Clock);
            await writer.ReplanAsync(session, recipientId, default);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await verify.SessionReminderHandoffs.CountAsync(h =>
            h.SessionId == session.Id && h.RecipientUserId == recipientId));
        SessionReminderHandoff afterSecondPlan = await verify.SessionReminderHandoffs.AsNoTracking()
            .SingleAsync(h => h.SessionId == session.Id && h.RecipientUserId == recipientId);
        Assert.Equal(SessionReminderHandoffState.Pending, afterSecondPlan.State);
        Assert.Equal(1, afterSecondPlan.Version);
    }

    [Fact]
    public async Task SessionReminderHandoffStore_CancelIsIdempotent_D082()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "handoff-cancel-organizer@example.test");
        UserId recipientId = await _factory.SeedUserAsync(email: "handoff-cancel-recipient@example.test");
        Session session = CreateSession(organizerId, minAttendance: 1);
        session.Invite(recipientId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(recipientId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Sessions.Add(session);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope planScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = planScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new SessionReminderHandoffStore(context, _factory.Clock)
                .ReplanAsync(session, recipientId, default);
            await context.SaveChangesAsync();
        }

        await using (AsyncServiceScope firstCancelScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = firstCancelScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new SessionReminderHandoffStore(context, _factory.Clock)
                .CancelAsync(session.Id, recipientId, default);
            await context.SaveChangesAsync();
        }

        SessionReminderHandoff afterFirstCancel;
        await using (AsyncServiceScope readScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
            afterFirstCancel = await context.SessionReminderHandoffs.AsNoTracking().SingleAsync(h =>
                h.SessionId == session.Id && h.RecipientUserId == recipientId);
        }

        Assert.Equal(SessionReminderHandoffState.Canceled, afterFirstCancel.State);
        Assert.Equal(1, afterFirstCancel.Version);

        await using (AsyncServiceScope secondCancelScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = secondCancelScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new SessionReminderHandoffStore(context, _factory.Clock)
                .CancelAsync(session.Id, recipientId, default);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionReminderHandoff afterSecondCancel = await verify.SessionReminderHandoffs.AsNoTracking()
            .SingleAsync(h => h.SessionId == session.Id && h.RecipientUserId == recipientId);
        Assert.Equal(SessionReminderHandoffState.Canceled, afterSecondCancel.State);
        Assert.Equal(1, afterSecondCancel.Version);
        Assert.Null(afterSecondCancel.DeliveredAt);
    }

    /// <summary>
    /// The §7.1/DOMAIN_MODEL §12 interpretation note this slice adds: a Delivered handoff whose
    /// recomputed due instant lands strictly in the future (a reschedule) resets to Pending against a
    /// real row, not just the in-memory contract SessionReminderHandoffTests already proved.
    /// </summary>
    [Fact]
    public async Task SessionReminderHandoffStore_DeliveredResetsToPendingWhenRescheduleMovesDueIntoTheFuture_D082()
    {
        UserId organizerId = await _factory.SeedUserAsync(email: "handoff-reset-organizer@example.test");
        UserId recipientId = await _factory.SeedUserAsync(email: "handoff-reset-recipient@example.test");
        // Start is 90 minutes out: far enough that a 1h-default reminder can have already fired
        // (Delivered) while the session itself has not started yet — the only way a recompute can
        // legitimately move a Delivered handoff's due instant back into the future without a start-time
        // change (which resets every non-removed participation to Pending, D-063, and would make the
        // recipient ineligible instead).
        Session session = CreateSession(organizerId, minAttendance: 1, leadMinutes: 90, durationMinutes: 60);
        session.Invite(recipientId, _factory.Clock);
        session.Rsvp(ParticipantRef.ForUser(recipientId), ParticipationStatus.Going, _factory.Clock);
        _ = session.DequeueEvents();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Sessions.Add(session);
            context.SessionReminderHandoffs.Add(new SessionReminderHandoff
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                RecipientUserId = recipientId,
                DueAt = _factory.Clock.GetCurrentInstant() - Duration.FromMinutes(5),
                State = SessionReminderHandoffState.Delivered,
                PlannedAt = _factory.Clock.GetCurrentInstant() - Duration.FromMinutes(10),
                DeliveredAt = _factory.Clock.GetCurrentInstant() - Duration.FromMinutes(5),
                Version = 3,
            });
            await context.SaveChangesAsync();
        }

        // Shrinking the recipient's personal offset from the 1h default to 15 minutes moves their due
        // instant from start-60m (already past) to start-15m (now+75m, strictly future) without
        // touching the participation status at all.
        session.SetReminderChoice(recipientId, ReminderOffset.Min15, _factory.Clock);

        await using (AsyncServiceScope rescheduleScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = rescheduleScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new SessionReminderHandoffStore(context, _factory.Clock)
                .ReplanAsync(session, recipientId, default);
            await context.SaveChangesAsync();
        }

        await using AsyncServiceScope verification = _factory.Services.CreateAsyncScope();
        AppDbContext verify = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        SessionReminderHandoff reset = await verify.SessionReminderHandoffs.AsNoTracking().SingleAsync(h =>
            h.SessionId == session.Id && h.RecipientUserId == recipientId);
        Assert.Equal(SessionReminderHandoffState.Pending, reset.State);
        Assert.Null(reset.DeliveredAt);
        Assert.Equal(4, reset.Version);
        Assert.True(reset.DueAt > _factory.Clock.GetCurrentInstant());
    }

    private Session CreateSession(
        UserId organizerId,
        int minAttendance,
        int leadMinutes = 60,
        int durationMinutes = 120)
    {
        SessionDetails details = new(
            SessionTitle.Create("Materializer adapter session"),
            _factory.Clock.GetCurrentInstant() + Duration.FromMinutes(leadMinutes),
            Duration15.FromMinutes(durationMinutes),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance,
            maxCapacity: null,
            isPrivate: true);
        Session session = Session.Create(organizerId, details, [], _factory.Clock);
        _ = session.DequeueEvents();
        return session;
    }
}
