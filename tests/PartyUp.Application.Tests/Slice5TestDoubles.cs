using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests;

internal sealed class FakeAccountGateway : IUserAccountGateway
{
    internal Dictionary<string, UserId> EmailUsers { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<UserId, PasswordCheckResult> PasswordChecks { get; } = [];

    internal Dictionary<UserId, AccountEmailInfo?> Emails { get; } = [];

    internal List<(UserId Id, string? Email, bool Confirmed, string? Password)> Created { get; } = [];

    internal int RealVerifies { get; private set; }

    internal int DummyVerifies { get; private set; }

    internal int PasswordSets { get; private set; }

    internal int Confirmations { get; private set; }

    public Task<UserId?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken) =>
        Task.FromResult(EmailUsers.TryGetValue(email, out UserId id) ? (UserId?)id : null);

    public Task<UserId?> FindUserIdByLoginAsync(
        string provider,
        string providerKey,
        CancellationToken cancellationToken) =>
        Task.FromResult<UserId?>(null);

    public Task CreateAccountAsync(
        UserId id,
        string? email,
        bool emailConfirmed,
        string? password,
        CancellationToken cancellationToken)
    {
        Created.Add((id, email, emailConfirmed, password));
        if (email is not null)
        {
            EmailUsers[email] = id;
            Emails[id] = new AccountEmailInfo(email, emailConfirmed);
        }

        return Task.CompletedTask;
    }

    public Task<PasswordCheckResult> CheckPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken)
    {
        PasswordCheckResult result = PasswordChecks.TryGetValue(id, out PasswordCheckResult? value)
            ? value
            : new PasswordCheckResult(false, false, false);
        if (result.HashVerified)
        {
            RealVerifies++;
        }

        return Task.FromResult(result);
    }

    public Task VerifyDummyPasswordAsync(CancellationToken cancellationToken)
    {
        DummyVerifies++;
        return Task.CompletedTask;
    }

    public Task SetPasswordAsync(
        UserId id,
        string password,
        CancellationToken cancellationToken)
    {
        PasswordSets++;
        return Task.CompletedTask;
    }

    public Task RemovePasswordAsync(UserId id, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task SetEmailAsync(
        UserId id,
        string email,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        Emails[id] = new AccountEmailInfo(email, confirmed);
        EmailUsers[email] = id;
        return Task.CompletedTask;
    }

    public Task ConfirmEmailAsync(UserId id, CancellationToken cancellationToken)
    {
        Confirmations++;
        return Task.CompletedTask;
    }

    public Task<AccountEmailInfo?> GetEmailAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        Task.FromResult(Emails.TryGetValue(id, out AccountEmailInfo? value) ? value : null);

    public Task AddLoginAsync(
        UserId id,
        string provider,
        string providerKey,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RemoveLoginAsync(
        UserId id,
        string provider,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListLoginProvidersAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task PurgeCredentialsAsync(UserId id, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureRoleExistsAsync(string role, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<bool> AddToRoleAsync(
        UserId id,
        string role,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

internal sealed class FakeTokenService : ISecurityTokenService
{
    internal List<IssueTokenRequest> Issues { get; } = [];

    internal List<RedeemTokenRequest> Redeems { get; } = [];

    internal Queue<TokenRedemption> RedemptionResults { get; } = [];

    public Task<IssuedToken> IssueAsync(
        IssueTokenRequest request,
        CancellationToken cancellationToken)
    {
        Issues.Add(request);
        return Task.FromResult(new IssuedToken(
            Guid.CreateVersion7(),
            $"raw-{Issues.Count}",
            Instant.FromUtc(2026, 8, 1, 0, 0)));
    }

    public Task<TokenRedemption> RedeemAsync(
        RedeemTokenRequest request,
        CancellationToken cancellationToken)
    {
        Redeems.Add(request);
        return Task.FromResult(RedemptionResults.Count > 0
            ? RedemptionResults.Dequeue()
            : new TokenRedemption(
                false,
                TokenRedemptionFailure.NotFound,
                request.Purpose,
                null,
                null,
                null,
                null,
                default));
    }
}

internal sealed class FakeEmailDispatcher : IEmailDispatcher
{
    internal List<EmailMessage> Messages { get; } = [];

    public Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    internal int Executions { get; private set; }

    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        Executions++;
        return work(cancellationToken);
    }
}

internal sealed class FakeUserRepository : IUserRepository
{
    internal Dictionary<UserId, User> Users { get; } = [];

    internal List<User> Added { get; } = [];

    public Task<User?> FindAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(Users.TryGetValue(id, out User? user) ? user : null);

    public Task<User?> FindForUpdateAsync(UserId id, CancellationToken cancellationToken) =>
        FindAsync(id, cancellationToken);

    public Task<IReadOnlyList<UserId>> FindDueForDeletionAsync(
        Instant now,
        int max,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserId>>([]);

    public void Add(User user)
    {
        Added.Add(user);
        Users[user.Id] = user;
    }
}

internal sealed class FakeFriendCodeGenerator : IFriendCodeGenerator
{
    public Task<FriendCode> NextAsync(CancellationToken cancellationToken) =>
        Task.FromResult(FriendCode.Create("ABCDEFGHJK"));
}

internal sealed class FakeAccountSessionService : IAccountSessionService
{
    internal int Rotations { get; private set; }

    public Task SignOutEverywhereAsync(UserId id, CancellationToken cancellationToken)
    {
        Rotations++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeCurrentUserAccessor(CurrentUser? currentUser) : ICurrentUserAccessor
{
    public Task<CurrentUser?> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(currentUser);
}

internal sealed class FakeUsernameRepository : IUsernameRegistryRepository
{
    internal Dictionary<string, UsernameRegistryEntry> Entries { get; } =
        new(StringComparer.Ordinal);

    internal int FindCalls { get; private set; }

    public Task<UsernameRegistryEntry?> FindAsync(
        string canonical,
        CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(
            Entries.TryGetValue(canonical, out UsernameRegistryEntry? entry) ? entry : null);
    }

    public Task<UsernameRegistryEntry?> FindActiveForOwnerAsync(
        UserId owner,
        CancellationToken cancellationToken) =>
        Task.FromResult(Entries.Values.SingleOrDefault(entry => entry.OwnerId == owner));

    public void Add(UsernameRegistryEntry entry) => Entries[entry.NameCanonical] = entry;
}

internal sealed class TestClock(Instant current) : IClock
{
    public Instant GetCurrentInstant() => current;
}
