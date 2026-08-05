using NodaTime;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;

namespace PartyUp.Application.Tests.Sessions;

public sealed class SessionCreateCommandTests
{
    [Fact]
    public async Task AppliesDefaultsAndRejectsOutOfWindowStartOrDuration_D129_D130_D142()
    {
        SessionTestFixture fixture = new();
        CreateSessionUseCase useCase = fixture.CreateUseCase();

        CreateSessionResult result = await useCase.ExecuteAsync(
            fixture.CreateCommand(durationMinutes: null, minAttendance: null, maxCapacity: null),
            default);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Session);
        Session session = result.Session;
        Assert.Equal(CreateSessionUseCase.DefaultMinAttendance, session.MinAttendance);
        Assert.Equal(CreateSessionUseCase.DefaultMaxCapacity, session.MaxCapacity);
        Assert.Equal(CreateSessionUseCase.DefaultDurationMinutes, session.Duration.TotalMinutes);
        Assert.Equal(Platform.NotApplicable, session.Platform);
        Assert.False(session.IsPrivate);

        // D-130: outside the [now+15m, now+1y] window.
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            fixture.CreateCommand(startTime: SessionTestFixture.Origin + Duration.FromMinutes(5)),
            default));

        // D-142: not a multiple of 15 minutes.
        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            fixture.CreateCommand(durationMinutes: 37),
            default));
    }

    [Fact]
    public async Task NonPrivateCreate_RequiresAnInvitedRegisteredFriend_D099()
    {
        SessionTestFixture fixture = new();
        CreateSessionUseCase useCase = fixture.CreateUseCase();

        await Assert.ThrowsAsync<DomainException>(() => useCase.ExecuteAsync(
            fixture.CreateCommand(isPrivate: false, inviteeIds: []),
            default));

        // A private, guest-only session needs no invitee at all (D-125).
        CreateSessionResult result = await useCase.ExecuteAsync(
            fixture.CreateCommand(isPrivate: true, inviteeIds: [], guestLinkCount: 2),
            default);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.GuestLinks!.Count);
    }

    [Fact]
    public async Task BulkInviteeAndGuestLinkCountsAreBounded()
    {
        SessionTestFixture fixture = new();
        CreateSessionUseCase useCase = fixture.CreateUseCase();

        List<UserId> tooManyInvitees = [.. Enumerable.Range(0, CreateSessionUseCase.MaxInviteeCount + 1)
            .Select(_ => UserId.New())];

        CreateSessionResult tooManyResult = await useCase.ExecuteAsync(
            fixture.CreateCommand(inviteeIds: tooManyInvitees),
            default);
        Assert.False(tooManyResult.Succeeded);
        Assert.Equal("session_validation", tooManyResult.Code);

        CreateSessionResult tooManyGuestLinksResult = await useCase.ExecuteAsync(
            fixture.CreateCommand(guestLinkCount: CreateSessionUseCase.MaxGuestLinkCount + 1),
            default);
        Assert.False(tooManyGuestLinksResult.Succeeded);
        Assert.Equal("session_validation", tooManyGuestLinksResult.Code);

        // A friend not accepted (or blocked) uniformly fails as user_not_found — never disclosing why.
        CreateSessionResult unknownInviteeResult = await useCase.ExecuteAsync(
            fixture.CreateCommand(inviteeIds: [UserId.New()]),
            default);
        Assert.False(unknownInviteeResult.Succeeded);
        Assert.Equal("user_not_found", unknownInviteeResult.Code);
    }
}
