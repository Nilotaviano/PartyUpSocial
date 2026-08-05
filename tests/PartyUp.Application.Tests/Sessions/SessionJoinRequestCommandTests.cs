using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionJoinRequestCommandTests
{
    [Fact]
    public async Task RequestsStayOpenUntilEndAndCarryNoAttendance_D098_D173()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = UserId.New();

        // In progress (after start, before end): D-173 keeps requests open.
        fixture.Clock.Advance(SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromMinutes(5));
        JoinRequestCommandResult result = await useCase.RequestJoinAsync(session.Id, requesterId, default);

        Assert.True(result.Succeeded);
        Assert.Equal(JoinRequestStatus.Pending, result.JoinRequest!.Status);
        // D-098: a request carries no attendance choice — it produces no participation at all.
        Assert.Null(session.Find(ParticipantRef.ForUser(requesterId)));

        // At/after the session's end, requests are closed.
        fixture.Clock.Advance(Duration.FromHours(3));
        await Assert.ThrowsAsync<DomainException>(
            () => useCase.RequestJoinAsync(session.Id, UserId.New(), default));
    }

    [Fact]
    public async Task ApprovalAndRejectionNotifyOnlyTheRequester_D107_D112()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = UserId.New();

        JoinRequestCommandResult requested = await useCase.RequestJoinAsync(session.Id, requesterId, default);
        JoinRequestCommandResult approved = await useCase.ApproveJoinRequestAsync(
            session.Id, fixture.OrganizerId, requested.JoinRequest!.Id, default);

        Assert.True(approved.Succeeded);
        Assert.Equal(ParticipationStatus.Going, session.Find(ParticipantRef.ForUser(requesterId))!.Status);
        Assert.Contains((session.Id, requesterId), fixture.Reminders.Replans);

        UserId rejectedRequesterId = UserId.New();
        JoinRequestCommandResult secondRequest = await useCase.RequestJoinAsync(session.Id, rejectedRequesterId, default);
        JoinRequestCommandResult rejected = await useCase.RejectJoinRequestAsync(
            session.Id, fixture.OrganizerId, secondRequest.JoinRequest!.Id, default);

        Assert.True(rejected.Succeeded);
        Assert.Null(session.Find(ParticipantRef.ForUser(rejectedRequesterId)));
    }

    [Fact]
    public async Task WithdrawnRequestIsIdempotentAndGrantsNoFurtherAccess_D043()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = UserId.New();

        await useCase.RequestJoinAsync(session.Id, requesterId, default);
        JoinRequestCommandResult withdrawn = await useCase.WithdrawJoinRequestAsync(session.Id, requesterId, default);
        Assert.True(withdrawn.Succeeded);

        // Idempotent: withdrawing again with no pending request is still a success, not an error.
        JoinRequestCommandResult withdrawnAgain = await useCase.WithdrawJoinRequestAsync(session.Id, requesterId, default);
        Assert.True(withdrawnAgain.Succeeded);

        // A withdrawn request grants no visibility/access — the organizer cannot approve it.
        JoinRequestId requestId = session.JoinRequests.Single().Id;
        await Assert.ThrowsAsync<DomainException>(
            () => useCase.ApproveJoinRequestAsync(session.Id, fixture.OrganizerId, requestId, default));
    }

    [Fact]
    public async Task RequestListIsOrganizerOnlyAndOtherwiseIndistinguishableFromMissing_D007()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = UserId.New();
        await useCase.RequestJoinAsync(session.Id, requesterId, default);

        (bool organizerFound, SessionPage<JoinRequestSummary> organizerPage) =
            await useCase.ListSessionJoinRequestsAsync(session.Id, fixture.OrganizerId, null, 20, default);
        Assert.True(organizerFound);
        Assert.Single(organizerPage.Items);

        (bool strangerFound, SessionPage<JoinRequestSummary> strangerPage) =
            await useCase.ListSessionJoinRequestsAsync(session.Id, UserId.New(), null, 20, default);
        Assert.False(strangerFound);
        Assert.Empty(strangerPage.Items);

        (bool missingFound, SessionPage<JoinRequestSummary> missingPage) = await useCase.ListSessionJoinRequestsAsync(
            SessionId.New(), fixture.OrganizerId, null, 20, default);
        Assert.False(missingFound);
        Assert.Empty(missingPage.Items);
    }
}
