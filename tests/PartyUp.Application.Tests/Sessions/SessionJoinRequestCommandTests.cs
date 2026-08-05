using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Friendships;
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
        // D-007: only an eligible friend of the organizer can see a non-private session and request
        // to join it — a totally unconnected stranger is invisible to it (D-065-adjacent fail-closed).
        UserId requesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);

        // In progress (after start, before end): D-173 keeps requests open.
        fixture.Clock.Advance(SessionTestFixture.DefaultStart - SessionTestFixture.Origin + Duration.FromMinutes(5));
        JoinRequestCommandResult result = await useCase.RequestJoinAsync(session.Id, requesterId, default);

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(JoinRequestStatus.Pending, result.JoinRequest!.Status);
        // D-098: a request carries no attendance choice — it produces no participation at all.
        Assert.Null(session.Find(ParticipantRef.ForUser(requesterId)));

        // At/after the session's end, requests are closed.
        UserId lateRequesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);
        fixture.Clock.Advance(Duration.FromHours(3));
        await Assert.ThrowsAsync<DomainException>(
            () => useCase.RequestJoinAsync(session.Id, lateRequesterId, default));
    }

    [Fact]
    public async Task ApprovalAndRejectionNotifyOnlyTheRequester_D107_D112()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);

        JoinRequestCommandResult requested = await useCase.RequestJoinAsync(session.Id, requesterId, default);
        JoinRequestCommandResult approved = await useCase.ApproveJoinRequestAsync(
            session.Id, fixture.OrganizerId, requested.JoinRequest!.Id, default);

        Assert.True(approved.Succeeded, approved.Code);
        Assert.Equal(ParticipationStatus.Going, session.Find(ParticipantRef.ForUser(requesterId))!.Status);
        Assert.Contains((session.Id, requesterId), fixture.Reminders.Replans);

        UserId rejectedRequesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);
        JoinRequestCommandResult secondRequest = await useCase.RequestJoinAsync(session.Id, rejectedRequesterId, default);
        JoinRequestCommandResult rejected = await useCase.RejectJoinRequestAsync(
            session.Id, fixture.OrganizerId, secondRequest.JoinRequest!.Id, default);

        Assert.True(rejected.Succeeded, rejected.Code);
        Assert.Null(session.Find(ParticipantRef.ForUser(rejectedRequesterId)));
    }

    [Fact]
    public async Task WithdrawnRequestIsIdempotentAndGrantsNoFurtherAccess_D043()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);

        await useCase.RequestJoinAsync(session.Id, requesterId, default);
        JoinRequestCommandResult withdrawn = await useCase.WithdrawJoinRequestAsync(session.Id, requesterId, default);
        Assert.True(withdrawn.Succeeded);

        // Idempotent: withdrawing again with no pending request is still a success, not an error.
        JoinRequestCommandResult withdrawnAgain = await useCase.WithdrawJoinRequestAsync(session.Id, requesterId, default);
        Assert.True(withdrawnAgain.Succeeded);

        // A withdrawn request grants no visibility/access — the organizer cannot approve it. Approving
        // a non-pending request is its own distinct, typed result (§8 join_request_not_pending), never
        // a leaked domain exception (F2).
        JoinRequestId requestId = session.JoinRequests.Single().Id;
        JoinRequestCommandResult approveResult = await useCase.ApproveJoinRequestAsync(
            session.Id, fixture.OrganizerId, requestId, default);
        Assert.False(approveResult.Succeeded);
        Assert.Equal("join_request_not_pending", approveResult.Code);
    }

    [Fact]
    public async Task RequestListIsOrganizerOnlyAndOtherwiseIndistinguishableFromMissing_D007()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);
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

    [Fact]
    public async Task RequestJoin_StrangerWithNoVisibilityIntoTheSession_IsIndistinguishableFromMissing_D065()
    {
        // F1: RequestJoinAsync must resolve visibility before writing anything -- a caller who is not
        // a friend of the organizer (or any extending participant) on a non-private session gets the
        // same 404 a missing session would, and writes no join request.
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId strangerId = UserId.New();

        JoinRequestCommandResult result = await useCase.RequestJoinAsync(session.Id, strangerId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("session_not_found", result.Code);
        Assert.Empty(session.JoinRequests);
    }

    [Fact]
    public async Task RequestJoin_BlockedPair_IsIndistinguishableFromMissingAndWritesNothing_D065()
    {
        SessionTestFixture fixture = new();
        Session session = fixture.SeedSession(minAttendance: 1, maxCapacity: null);
        SessionJoinRequestUseCases useCase = fixture.JoinRequestUseCases();
        UserId requesterId = fixture.Friendships.MakeAcceptedWith(fixture.OrganizerId, fixture.Clock);
        fixture.Blocks.Add(Block.Create(
            fixture.OrganizerId, requesterId, Username.Create("requesterblk"), fixture.Clock));

        JoinRequestCommandResult result = await useCase.RequestJoinAsync(session.Id, requesterId, default);

        Assert.False(result.Succeeded);
        Assert.Equal("session_not_found", result.Code);
        Assert.Empty(session.JoinRequests);
    }
}
