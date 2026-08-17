using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Polls;
using PartyUp.Application.Sessions;
using PartyUp.Domain.FriendGroups;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.FriendGroups;

/// <summary>
/// PHASE5-style SQL characterization for rule 2g (D-198), direct-EF-seeded like
/// <c>SessionVisibilitySqlCharacterizationTests</c>: the leak regression this design exists to close
/// is a friend-of-an-admitted-member NOT inheriting access the way rule 2 would for a Public target.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GroupVisibilitySqlTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GroupsSessionAdmitsOwnerAndAcceptedMember_ExcludesFriendOfMember_D198()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId ownerId = await _factory.SeedUserAsync(email: $"gvis-owner-{Guid.NewGuid():N}@example.test");
        UserId memberId = await _factory.SeedUserAsync(email: $"gvis-member-{Guid.NewGuid():N}@example.test");
        UserId friendOfMemberId = await _factory.SeedUserAsync(email: $"gvis-fom-{Guid.NewGuid():N}@example.test");
        UserId strangerId = await _factory.SeedUserAsync(email: $"gvis-stranger-{Guid.NewGuid():N}@example.test");

        FriendGroupId groupId = await CreateGroupWithAcceptedMemberAsync(context, ownerId, memberId);
        // The leak this design closes: friendOfMemberId is a genuine friend of an admitted member, but
        // that is NOT a path into a Groups target — only owner/Accepted-member of the group is.
        await LinkFriendsAsync(context, memberId, friendOfMemberId);

        SessionId groupsSessionId = await CreateGroupsSessionAsync(context, ownerId, groupId);

        SessionPage<SessionSummary> ownerUpcoming = await reads.ListUpcomingAsync(ownerId, null, 20, default);
        SessionPage<SessionSummary> memberUpcoming = await reads.ListUpcomingAsync(memberId, null, 20, default);
        SessionPage<SessionSummary> friendOfMemberUpcoming =
            await reads.ListUpcomingAsync(friendOfMemberId, null, 20, default);
        SessionPage<SessionSummary> strangerUpcoming = await reads.ListUpcomingAsync(strangerId, null, 20, default);

        Assert.Contains(groupsSessionId.Value, ownerUpcoming.Items.Select(i => i.Id.Value));
        Assert.Contains(groupsSessionId.Value, memberUpcoming.Items.Select(i => i.Id.Value));
        Assert.DoesNotContain(groupsSessionId.Value, friendOfMemberUpcoming.Items.Select(i => i.Id.Value));
        Assert.DoesNotContain(groupsSessionId.Value, strangerUpcoming.Items.Select(i => i.Id.Value));
    }

    [Fact]
    public async Task DiscoverShowsAGroupsSessionToAnUnattachedAcceptedMember_D198()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId ownerId = await _factory.SeedUserAsync(email: $"gvis-disc-owner-{Guid.NewGuid():N}@example.test");
        UserId memberId = await _factory.SeedUserAsync(email: $"gvis-disc-member-{Guid.NewGuid():N}@example.test");

        FriendGroupId groupId = await CreateGroupWithAcceptedMemberAsync(context, ownerId, memberId);
        SessionId groupsSessionId = await CreateGroupsSessionAsync(context, ownerId, groupId);

        SessionPage<SessionSummary> memberDiscover = await reads.ListDiscoverAsync(memberId, null, 20, default);
        Assert.Contains(groupsSessionId.Value, memberDiscover.Items.Select(i => i.Id.Value));
    }

    [Fact]
    public async Task VisibilityEqualsZeroGateStillInheritsPublicAndStillHidesPrivate_D018_D043()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId organizerId = await _factory.SeedUserAsync(email: $"gvis-gate-org-{Guid.NewGuid():N}@example.test");
        UserId friendId = await _factory.SeedUserAsync(email: $"gvis-gate-friend-{Guid.NewGuid():N}@example.test");

        await LinkFriendsAsync(context, organizerId, friendId);

        SessionId publicSessionId = await CreatePlainSessionAsync(
            context, organizerId, ContentVisibility.Public, invitees: [friendId]);
        SessionId privateSessionId = await CreatePlainSessionAsync(
            context, organizerId, ContentVisibility.Private, invitees: []);

        SessionPage<SessionSummary> friendUpcoming = await reads.ListUpcomingAsync(friendId, null, 20, default);
        Assert.Contains(publicSessionId.Value, friendUpcoming.Items.Select(i => i.Id.Value));
        Assert.DoesNotContain(privateSessionId.Value, friendUpcoming.Items.Select(i => i.Id.Value));
    }

    [Fact]
    public async Task GroupsPollAdmitsOwnerAndAcceptedMember_ExcludesFriendOfMember_D198()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<IPollReadRepository>();

        UserId ownerId = await _factory.SeedUserAsync(email: $"gvis-poll-owner-{Guid.NewGuid():N}@example.test");
        UserId memberId = await _factory.SeedUserAsync(email: $"gvis-poll-member-{Guid.NewGuid():N}@example.test");
        UserId friendOfMemberId = await _factory.SeedUserAsync(email: $"gvis-poll-fom-{Guid.NewGuid():N}@example.test");

        FriendGroupId groupId = await CreateGroupWithAcceptedMemberAsync(context, ownerId, memberId);
        await LinkFriendsAsync(context, memberId, friendOfMemberId);

        PollId groupsPollId = await CreateGroupsPollAsync(context, ownerId, groupId);

        SessionPage<PollSummary> ownerInvolved = await reads.ListInvolvedAsync(ownerId, null, 20, default);
        SessionPage<PollSummary> memberDiscover = await reads.ListDiscoverableAsync(memberId, null, 20, default);
        SessionPage<PollSummary> friendOfMemberDiscover =
            await reads.ListDiscoverableAsync(friendOfMemberId, null, 20, default);

        // The organizer reaches their own Groups poll through rule 1 (involvement), not rule 2g.
        Assert.Contains(groupsPollId.Value, ownerInvolved.Items.Select(i => i.Id.Value));
        Assert.Contains(groupsPollId.Value, memberDiscover.Items.Select(i => i.Id.Value));
        Assert.DoesNotContain(groupsPollId.Value, friendOfMemberDiscover.Items.Select(i => i.Id.Value));
    }

    /// <summary>
    /// D-205's zero-group degenerate state: once the targeted group is hard-deleted the junction row
    /// is gone too (FK cascade), and the session degrades to rule-1-only — the former Accepted member
    /// loses access, the organizer (rule 1) keeps it.
    /// </summary>
    [Fact]
    public async Task EmptyAudienceAfterGroupDeletionIsVisibleToRule1HoldersOnly_D205()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reads = scope.ServiceProvider.GetRequiredService<ISessionReadRepository>();

        UserId ownerId = await _factory.SeedUserAsync(email: $"gvis-empty-owner-{Guid.NewGuid():N}@example.test");
        UserId memberId = await _factory.SeedUserAsync(email: $"gvis-empty-member-{Guid.NewGuid():N}@example.test");

        FriendGroupId groupId = await CreateGroupWithAcceptedMemberAsync(context, ownerId, memberId);
        SessionId groupsSessionId = await CreateGroupsSessionAsync(context, ownerId, groupId);

        // Hard-delete the group directly at the database boundary — the junction row cascades away,
        // leaving the session's audience empty (D-205's degenerate state).
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM friend_groups WHERE id = {groupId.Value}");

        SessionPage<SessionSummary> ownerUpcoming = await reads.ListUpcomingAsync(ownerId, null, 20, default);
        SessionPage<SessionSummary> memberUpcoming = await reads.ListUpcomingAsync(memberId, null, 20, default);

        Assert.Contains(groupsSessionId.Value, ownerUpcoming.Items.Select(i => i.Id.Value));
        Assert.DoesNotContain(groupsSessionId.Value, memberUpcoming.Items.Select(i => i.Id.Value));
    }

    // ---- shared seeding -------------------------------------------------------------------------

    private async Task<FriendGroupId> CreateGroupWithAcceptedMemberAsync(
        AppDbContext context, UserId ownerId, UserId memberId)
    {
        FriendGroup group = FriendGroup.Create(ownerId, GroupName.Create($"SQL fixture {Guid.NewGuid():N}"[..20]), _factory.Clock);
        group.Invite(ownerId, memberId, isAcceptedFriendOfOwner: true, _factory.Clock);
        group.AcceptInvite(memberId, _factory.Clock);
        _ = group.DequeueEvents();
        context.FriendGroups.Add(group);
        await context.SaveChangesAsync();
        return group.Id;
    }

    private async Task<SessionId> CreateGroupsSessionAsync(AppDbContext context, UserId organizerId, FriendGroupId groupId)
    {
        SessionDetails details = new(
            SessionTitle.Create("Groups SQL fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Groups);

        Session session = Session.Create(organizerId, details, invitees: [], _factory.Clock, [groupId]);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<SessionId> CreatePlainSessionAsync(
        AppDbContext context, UserId organizerId, ContentVisibility visibility, IReadOnlyCollection<UserId> invitees)
    {
        SessionDetails details = new(
            SessionTitle.Create("Gate SQL fixture"),
            _factory.Clock.GetCurrentInstant() + Duration.FromHours(3),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: visibility);

        Session session = Session.Create(organizerId, details, invitees, _factory.Clock);
        _ = session.DequeueEvents();
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<PollId> CreateGroupsPollAsync(AppDbContext context, UserId organizerId, FriendGroupId groupId)
    {
        Instant baseTime = _factory.Clock.GetCurrentInstant() + Duration.FromDays(1);
        PollDetails details = new(
            SessionTitle.Create("Groups poll SQL fixture"),
            Duration15.FromMinutes(60),
            Platform.NotApplicable,
            null,
            SessionNotes.Empty,
            minAttendance: 1,
            maxCapacity: null,
            visibility: ContentVisibility.Groups);

        Poll poll = Poll.Create(
            organizerId,
            details,
            PollDeadlineChoice.Hours48,
            [baseTime, baseTime + Duration.FromHours(1)],
            invitees: [],
            _factory.Clock,
            [groupId]);
        _ = poll.DequeueEvents();
        context.Polls.Add(poll);
        await context.SaveChangesAsync();
        return poll.Id;
    }

    private async Task LinkFriendsAsync(AppDbContext context, UserId a, UserId b)
    {
        Friendship friendship = Friendship.Request(
            await context.DomainUsers.SingleAsync(u => u.Id == a),
            await context.DomainUsers.SingleAsync(u => u.Id == b),
            _factory.Clock);
        friendship.Accept(b, _factory.Clock);
        _ = friendship.DequeueEvents();
        context.Friendships.Add(friendship);
        await context.SaveChangesAsync();
    }
}
