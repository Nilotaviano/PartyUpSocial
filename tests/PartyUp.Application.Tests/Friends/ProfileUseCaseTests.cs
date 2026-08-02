using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Friends;
using PartyUp.Domain.Friendships;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Visibility;

namespace PartyUp.Application.Tests.Friends;

public sealed class ProfileUseCaseTests
{
    [Fact]
    public async Task Execute_ReturnsOnlyUsernameBeforeAcceptance_D068()
    {
        User subject = FriendTestData.Active("alex", "Alex");
        var resolver = new FriendTestResolver
        {
            Result = FriendTestData.Resolved(subject, ProfileDisclosure.UsernameOnly),
        };
        var useCase = new GetProfileUseCase(resolver);

        UsernameProfileResolution? result = await useCase.ExecuteAsync(
            UserId.New(), "  ALEX  ", default);

        UsernameOnlyProfile profile = Assert.IsType<UsernameOnlyProfile>(result!.Profile);
        Assert.Equal("username_only", profile.Disclosure);
        Assert.Equal("alex", profile.Username);
        Assert.Equal("alex", resolver.LastUsername);
        Assert.Equal(
            ["Disclosure", "Relationship", "Username"],
            profile.GetType().GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(["Profile"], result.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Execute_ReturnsFullProfileToAnAcceptedFriend_D032()
    {
        User subject = FriendTestData.Active("alex", "Alex");
        subject.LinkPlatform(Platform.Steam, GamingHandle.Create("alex-steam"));
        var resolver = new FriendTestResolver
        {
            Result = FriendTestData.Resolved(
                subject,
                ProfileDisclosure.Full,
                FriendRelationshipKind.Accepted),
        };

        UsernameProfileResolution? result = await new GetProfileUseCase(resolver)
            .ExecuteAsync(UserId.New(), "alex", default);

        FullProfile profile = Assert.IsType<FullProfile>(result!.Profile);
        Assert.Equal("full", profile.Disclosure);
        Assert.Equal("Alex", profile.DisplayName);
        Assert.Equal(subject.Avatar, profile.Avatar);
        Assert.Equal("alex-steam", Assert.Single(profile.Platforms).Handle);
    }

    [Fact]
    public async Task Execute_HidesBannedStrangerButReturnsFullToAcceptedFriend_D180()
    {
        User subject = FriendTestData.Active("alex", "Alex");
        subject.Ban(FriendTestData.Clock());
        var hidden = new GetProfileUseCase(new FriendTestResolver());
        var accepted = new GetProfileUseCase(new FriendTestResolver
        {
            Result = FriendTestData.Resolved(
                subject,
                ProfileDisclosure.Full,
                FriendRelationshipKind.Accepted),
        });

        Assert.Null(await hidden.ExecuteAsync(UserId.New(), "alex", default));
        UsernameProfileResolution? visible = await accepted.ExecuteAsync(UserId.New(), "alex", default);
        Assert.IsType<FullProfile>(visible!.Profile);
    }

    [Fact]
    public async Task Execute_VisibilityBackedResolverHidesEitherDirectionOfBlockAsMissing_D065()
    {
        User viewer = FriendTestData.Active("viewer", "Viewer");
        User subject = FriendTestData.Active("alex", "Alex");
        var useCase = new GetProfileUseCase(
            new FriendTestResolver
            {
                Candidate = subject,
                Blocks = [(viewer.Id, subject.Id)],
            });

        UsernameProfileResolution? blockedByViewer = await useCase.ExecuteAsync(
            viewer.Id, "alex", default);

        useCase = new GetProfileUseCase(
            new FriendTestResolver
            {
                Candidate = subject,
                Blocks = [(subject.Id, viewer.Id)],
            });
        UsernameProfileResolution? viewerBlocked = await useCase.ExecuteAsync(
            viewer.Id, "alex", default);

        Assert.Null(blockedByViewer);
        Assert.Null(viewerBlocked);
    }
}

internal static class FriendTestData
{
    public static TestClock Clock() => new(Instant.FromUtc(2026, 8, 1, 12, 0));

    public static User Active(string username, string displayName)
    {
        TestClock clock = Clock();
        User user = User.BeginOnboarding(
            UserId.New(),
            FriendCode.Create("23456789AB"),
            AgeGateSource.SelfAttested,
            clock);
        user.CompleteOnboarding(
            Username.Create(username),
            DisplayName.Create(displayName),
            Avatar.FromEmoji("🎮"),
            DateTimeZoneProviders.Tzdb["America/Fortaleza"],
            Locale.Create("en"),
            clock);
        return user;
    }

    public static ResolvedUserGraph Resolved(
        User user,
        ProfileDisclosure disclosure,
        FriendRelationshipKind relationship = FriendRelationshipKind.None) =>
        new(
            user,
            disclosure,
            new FriendRelationship(
                relationship,
                relationship == FriendRelationshipKind.None ? null : FriendshipId.New()));
}

internal sealed class FriendTestResolver : IUserGraphResolver
{
    public ResolvedUserGraph? Result { get; init; }

    public User? Candidate { get; init; }

    public IReadOnlyCollection<(UserId BlockerId, UserId BlockedId)> Blocks { get; init; } = [];

    public string? LastUsername { get; private set; }

    public string? LastFriendCode { get; private set; }

    public Task<ResolvedUserGraph?> ResolveSelfAsync(
        UserId viewerId,
        CancellationToken cancellationToken) => Task.FromResult(Result);

    public Task<ResolvedUserGraph?> ResolveProfileByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken)
    {
        LastUsername = canonicalUsername;
        return Task.FromResult(ResolveProfile(viewerId));
    }

    public Task<ResolvedUserGraph?> ResolveProfileByFriendCodeAsync(
        UserId viewerId,
        string canonicalFriendCode,
        CancellationToken cancellationToken)
    {
        LastFriendCode = canonicalFriendCode;
        return Task.FromResult(Result);
    }

    public Task<ResolvedUserGraph?> ResolveFriendTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) => Task.FromResult(Result);

    public Task<ResolvedUserGraph?> ResolveBlockTargetByUsernameAsync(
        UserId viewerId,
        string canonicalUsername,
        CancellationToken cancellationToken) => Task.FromResult(Result);

    private ResolvedUserGraph? ResolveProfile(UserId viewerId)
    {
        if (Candidate is null)
        {
            return Result;
        }

        bool isBlocked = Blocks.Any(block =>
            (block.BlockerId == viewerId && block.BlockedId == Candidate.Id) ||
            (block.BlockerId == Candidate.Id && block.BlockedId == viewerId));
        Viewer viewer = Viewer.Create(
            viewerId,
            blockedCounterpartIds: isBlocked ? [Candidate.Id] : null);
        ProfileDisclosure disclosure = ProfileVisibility.For(Candidate, viewer);
        return disclosure == ProfileDisclosure.Hidden ? null : FriendTestData.Resolved(Candidate, disclosure);
    }

}
internal sealed class FriendTestFriendshipRepository : IFriendshipRepository
{
    public IReadOnlyList<AcceptedFriendProjection> Accepted { get; init; } = [];

    public IReadOnlyList<PendingFriendProjection> Pending { get; init; } = [];

    public Task<Friendship?> FindPairAsync(
        UserId first, UserId second, CancellationToken cancellationToken) => Task.FromResult<Friendship?>(null);

    public Task<Friendship?> FindPairForUpdateAsync(
        UserId first, UserId second, CancellationToken cancellationToken) => Task.FromResult<Friendship?>(null);

    public Task<FriendshipPair?> FindOwnedPairAsync(
        FriendshipId friendshipId, UserId actorId, CancellationToken cancellationToken) =>
        Task.FromResult<FriendshipPair?>(null);

    public Task<Friendship?> FindByIdForUpdateAsync(
        FriendshipId friendshipId, CancellationToken cancellationToken) => Task.FromResult<Friendship?>(null);

    public Task<IReadOnlyList<AcceptedFriendProjection>> ListAcceptedAsync(
        UserId userId, CancellationToken cancellationToken) => Task.FromResult(Accepted);

    public Task<IReadOnlyList<PendingFriendProjection>> ListPendingAsync(
        UserId userId, CancellationToken cancellationToken) => Task.FromResult(Pending);

    public void Add(Friendship friendship)
    {
    }

    public void Remove(Friendship friendship)
    {
    }
}

internal sealed class FriendTestBlockRepository : IBlockRepository
{
    public IReadOnlyList<BlockedUserProjection> Outbound { get; init; } = [];

    public Task<IReadOnlyList<Block>> FindPairForUpdateAsync(
        UserId first, UserId second, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Block>>([]);

    public Task<BlockPair?> FindOutboundOwnedPairAsync(
        BlockId blockId, UserId blockerId, CancellationToken cancellationToken) =>
        Task.FromResult<BlockPair?>(null);

    public Task<Block?> FindByIdForUpdateAsync(BlockId blockId, CancellationToken cancellationToken) =>
        Task.FromResult<Block?>(null);

    public Task<IReadOnlyList<BlockedUserProjection>> ListOutboundAsync(
        UserId blockerId, CancellationToken cancellationToken) => Task.FromResult(Outbound);

    public void Add(Block block)
    {
    }

    public void Remove(Block block)
    {
    }
}
