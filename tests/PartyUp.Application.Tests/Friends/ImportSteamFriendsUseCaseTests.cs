using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Friends;

public sealed class ImportSteamFriendsUseCaseTests
{
    [Fact]
    public async Task Execute_ExcludesBlockedBannedPendingAcceptedAndTombstonedMatches_D049()
    {
        SteamImportFixture fixture = new();
        fixture.AddCandidate("eligible", FriendTestData.Active("eligible", "Eligible"));
        fixture.AddCandidate(
            "blocked",
            FriendTestData.Active("blocked", "Blocked"),
            isBlocked: true);
        User banned = FriendTestData.Active("banned", "Banned");
        banned.Ban(FriendTestData.Clock());
        fixture.AddCandidate("banned", banned);
        fixture.AddCandidate(
            "pending",
            FriendTestData.Active("pending", "Pending"),
            FriendRelationshipKind.OutgoingPending);
        fixture.AddCandidate(
            "accepted",
            FriendTestData.Active("accepted", "Accepted"),
            FriendRelationshipKind.Accepted);
        User tombstone = FriendTestData.Active("tombstone", "Tombstone");
        tombstone.RequestDeletion(FriendTestData.Clock());
        tombstone.MarkDeleted(new TestClock(
            FriendTestData.Clock().GetCurrentInstant() + Duration.FromDays(8)));
        fixture.AddCandidate("tombstone", tombstone);

        SteamImportReady result = Assert.IsType<SteamImportReady>(
            await fixture.ExecuteAsync());

        Assert.Equal("eligible", Assert.Single(result.Matches).Username);
        Assert.Equal(
            ["banned", "blocked", "tombstone"],
            result.Unmatched.Select(item => item.SteamId64).Order().ToArray());
    }

    [Fact]
    public async Task Execute_OmitsAcceptedAndPendingFromMatchesAndUnmatchedCards_D049()
    {
        SteamImportFixture fixture = new();
        fixture.AddCandidate(
            "accepted",
            FriendTestData.Active("accepted", "Accepted"),
            FriendRelationshipKind.Accepted);
        fixture.AddCandidate(
            "pending",
            FriendTestData.Active("pending", "Pending"),
            FriendRelationshipKind.IncomingPending);

        SteamImportZeroMatches result = Assert.IsType<SteamImportZeroMatches>(
            await fixture.ExecuteAsync());

        Assert.Empty(result.Matches);
        Assert.Empty(result.Unmatched);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_ClassifiesHiddenAndAbsentAsEquivalentUnmatchedInvitationCards_D049()
    {
        SteamImportFixture fixture = new();
        fixture.AddCandidate(
            "hidden",
            FriendTestData.Active("hidden", "Hidden"),
            isBlocked: true);
        fixture.AddFriend("absent");

        SteamImportZeroMatches result = Assert.IsType<SteamImportZeroMatches>(
            await fixture.ExecuteAsync());

        SteamUnmatchedFriend hidden = result.Unmatched.Single(item => item.SteamId64 == "hidden");
        SteamUnmatchedFriend absent = result.Unmatched.Single(item => item.SteamId64 == "absent");
        Assert.Equal(hidden.PersonaName, absent.PersonaName);
        Assert.Equal(hidden.AvatarUrl, absent.AvatarUrl);
        Assert.Equal(hidden.InvitationUrl, absent.InvitationUrl);
        Assert.Empty(fixture.Notifications.Added);
    }

    [Fact]
    public async Task Execute_UsesTheImportersOwnAddLinkForEveryUnmatchedFriend_D049()
    {
        SteamImportFixture fixture = new();
        fixture.AddFriend("first");
        fixture.AddFriend("second");

        SteamImportZeroMatches result = Assert.IsType<SteamImportZeroMatches>(
            await fixture.ExecuteAsync());

        Assert.All(
            result.Unmatched,
            item => Assert.Equal(fixture.InvitationUrl, item.InvitationUrl));
        Assert.Equal(fixture.InvitationUrl, result.InvitationUrl);
    }

    [Fact]
    public async Task Execute_InvitationLinkContainsTheStableFriendCode_D144()
    {
        SteamImportFixture fixture = new();
        fixture.AddFriend("absent");

        SteamImportZeroMatches result = Assert.IsType<SteamImportZeroMatches>(
            await fixture.ExecuteAsync());

        Assert.EndsWith($"/add/{fixture.Importer.FriendCode!.Value}", result.InvitationUrl);
    }

    [Theory]
    [InlineData(ProviderCase.Private)]
    [InlineData(ProviderCase.ZeroFriends)]
    [InlineData(ProviderCase.ZeroMatches)]
    [InlineData(ProviderCase.NotConfigured)]
    [InlineData(ProviderCase.UpstreamFailure)]
    public async Task Execute_ReturnsAllFiveFirstClassProviderStates_D049(
        ProviderCase providerCase)
    {
        SteamImportFixture fixture = new();
        fixture.Client.Result = providerCase switch
        {
            ProviderCase.Private => new SteamFriendListPrivateProfile(),
            ProviderCase.ZeroFriends => new SteamFriendListSuccess([]),
            ProviderCase.ZeroMatches => new SteamFriendListSuccess(
                [new SteamFriend("absent", null, null)]),
            ProviderCase.NotConfigured => new SteamFriendListNotConfigured(),
            ProviderCase.UpstreamFailure => new SteamFriendListUpstreamFailure(),
            _ => throw new ArgumentOutOfRangeException(nameof(providerCase)),
        };

        ImportSteamFriendsResult result = await fixture.ExecuteAsync();

        Type expected = providerCase switch
        {
            ProviderCase.Private => typeof(SteamImportPrivateProfile),
            ProviderCase.ZeroFriends => typeof(SteamImportZeroFriends),
            ProviderCase.ZeroMatches => typeof(SteamImportZeroMatches),
            ProviderCase.NotConfigured => typeof(SteamImportNotConfigured),
            ProviderCase.UpstreamFailure => typeof(SteamImportUpstreamFailure),
            _ => throw new ArgumentOutOfRangeException(nameof(providerCase)),
        };
        Assert.IsType(expected, result);
    }

    [Fact]
    public async Task Execute_NotifiesOnlyTheImporterAndCreatesNoCorrelationState_D105()
    {
        SteamImportFixture fixture = new();
        User matched = FriendTestData.Active("matched", "Matched");
        fixture.AddCandidate("matched", matched);

        SteamImportReady result = Assert.IsType<SteamImportReady>(
            await fixture.ExecuteAsync());

        Assert.Single(result.Matches);
        Notification notification = Assert.Single(fixture.Notifications.Added);
        Assert.Equal(fixture.Importer.Id, notification.RecipientUserId);
        Assert.Equal(matched.Id, notification.ActorUserId);
        Assert.Equal(NotificationType.SteamImportMatch, notification.Type);
    }

    public enum ProviderCase
    {
        Private,
        ZeroFriends,
        ZeroMatches,
        NotConfigured,
        UpstreamFailure,
    }
}

internal sealed class SteamImportFixture
{
    private readonly SteamImportUserRepository _users;
    private readonly ImportSteamFriendsUseCase _useCase;

    internal SteamImportFixture()
    {
        Importer = FriendTestData.Active("importer", "Importer");
        _users = new SteamImportUserRepository(Importer);
        Gateway.ImporterId = Importer.Id;
        Gateway.ImporterSteamId64 = "importer-steam";
        _useCase = new ImportSteamFriendsUseCase(
            Client,
            Gateway,
            _users,
            new SteamImportLinkBuilder(),
            Notifications,
            new SteamImportUnitOfWork(),
            FriendTestData.Clock());
    }

    internal User Importer { get; }

    internal FakeSteamFriendListClient Client { get; } = new();

    internal FakeSteamImportAccountGateway Gateway { get; } = new();

    internal SteamImportNotificationRepository Notifications { get; } = new();

    internal string InvitationUrl =>
        $"https://partyup.test/add/{Importer.FriendCode!.Value}";

    internal void AddCandidate(
        string steamId64,
        User user,
        FriendRelationshipKind relationship = FriendRelationshipKind.None,
        bool isBlocked = false)
    {
        AddFriend(steamId64, "Steam Friend", "https://cdn.test/avatar.png");
        Gateway.Candidates[steamId64] = new SteamImportCandidate(
            steamId64,
            user,
            relationship,
            isBlocked);
    }

    internal void AddFriend(
        string steamId64,
        string? personaName = "Steam Friend",
        string? avatarUrl = "https://cdn.test/avatar.png")
    {
        SteamFriendListSuccess current = Assert.IsType<SteamFriendListSuccess>(Client.Result);
        Client.Result = new SteamFriendListSuccess(
            current.Friends.Append(new SteamFriend(
                steamId64,
                personaName,
                avatarUrl)).ToArray());
    }

    internal Task<ImportSteamFriendsResult> ExecuteAsync() =>
        _useCase.ExecuteAsync(Importer.Id, default);
}

internal sealed class FakeSteamFriendListClient : ISteamFriendListClient
{
    internal SteamFriendListResult Result { get; set; } = new SteamFriendListSuccess([]);

    public Task<SteamFriendListResult> GetAsync(
        string steamId64,
        CancellationToken cancellationToken) => Task.FromResult(Result);
}

internal sealed class FakeSteamImportAccountGateway : ISteamImportAccountGateway
{
    internal UserId ImporterId { get; set; }

    internal string? ImporterSteamId64 { get; set; }

    internal Dictionary<string, SteamImportCandidate> Candidates { get; } =
        new(StringComparer.Ordinal);

    public Task<string?> FindSteamIdAsync(
        UserId id,
        CancellationToken cancellationToken) => Task.FromResult(ImporterSteamId64);

    public Task<IReadOnlyDictionary<string, UserId>> FindSteamIdentityOwnersAsync(
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, UserId>>(
            Candidates
                .Where(pair => steamIds.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.User.Id,
                    StringComparer.Ordinal));

    public Task LockUsersAsync(
        IReadOnlyCollection<UserId> userIds,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<SteamImportOwnershipSnapshot> ReadSteamImportOwnershipSnapshotAsync(
        UserId importerId,
        IReadOnlyCollection<string> steamIds,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SteamImportOwnershipSnapshot(
            ImporterSteamId64,
            Candidates
                .Where(pair => steamIds.Contains(pair.Key))
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)));
}

internal sealed class SteamImportUserRepository(User importer) : IUserRepository
{
    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(id == importer.Id ? importer : null);

    public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        FindAsync(id, cancellationToken);

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserId>>([]);

    public void Add(User user) => throw new NotSupportedException();
}

internal sealed class SteamImportNotificationRepository : INotificationRepository
{
    internal List<Notification> Added { get; } = [];

    public void Add(Notification notification) => Added.Add(notification);

    public Task<Notification?> FindAsync(NotificationId id, CancellationToken cancellationToken) =>
        Task.FromResult(Added.Find(notification => notification.Id == id));

    public Task AddSteamMatchIfAbsentAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        Added.Add(notification);
        return Task.CompletedTask;
    }

    public Task DeleteForRecipientAsync(
        UserId recipientUserId,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SteamImportLinkBuilder : IPublicLinkBuilder
{
    public string Profile(Username username) => $"https://partyup.test/u/{username.Value}";

    public string Add(FriendCode friendCode) => $"https://partyup.test/add/{friendCode.Value}";
}

internal sealed class SteamImportUnitOfWork : IUnitOfWork
{
    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken) => work(cancellationToken);
}
