using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
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

    internal Dictionary<(string Provider, string ProviderKey), UserId> LoginUsers { get; } = [];

    internal Queue<UserId?> EmailLookupResults { get; } = [];

    internal List<(UserId Id, string? Email, bool Confirmed, string? Password)> Created { get; } = [];

    internal List<(UserId Id, string Provider, string ProviderKey)> Logins { get; } = [];

    internal Dictionary<UserId, List<string>> LoginProviders { get; } = [];

    internal List<(UserId Id, string Provider)> RemovedLogins { get; } = [];

    internal HashSet<UserId> PasswordUsers { get; } = [];

    internal List<(UserId Id, string SteamId64, string PersonaName, Instant FetchedAt)>
        SteamIdentities { get; } = [];

    internal List<UserId> RemovedSteamIdentities { get; } = [];

    internal List<UserId> PurgedCredentials { get; } = [];

    internal int RealVerifies { get; private set; }

    internal int DummyVerifies { get; private set; }

    internal int PasswordSets { get; private set; }

    internal int Confirmations { get; private set; }

    public Task<UserId?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken) =>
        Task.FromResult(EmailLookupResults.Count > 0
            ? EmailLookupResults.Dequeue()
            : EmailUsers.TryGetValue(email, out UserId id) ? (UserId?)id : null);

    public Task<UserId?> FindUserIdByLoginAsync(
        string provider,
        string providerKey,
        CancellationToken cancellationToken) =>
        Task.FromResult(LoginUsers.TryGetValue((provider, providerKey), out UserId id)
            ? (UserId?)id
            : null);

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

    public Task RemovePasswordAsync(UserId id, CancellationToken cancellationToken)
    {
        PasswordUsers.Remove(id);
        return Task.CompletedTask;
    }

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
        CancellationToken cancellationToken)
    {
        Logins.Add((id, provider, providerKey));
        LoginUsers[(provider, providerKey)] = id;
        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(
        UserId id,
        string provider,
        CancellationToken cancellationToken)
    {
        RemovedLogins.Add((id, provider));
        Logins.RemoveAll(login => login.Id == id && login.Provider == provider);
        LoginProviders.GetValueOrDefault(id)?.RemoveAll(value => value == provider);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListLoginProvidersAsync(
        UserId id,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(
            LoginProviders.TryGetValue(id, out List<string>? providers) ? providers : []);

    public Task<bool> HasPasswordAsync(UserId id, CancellationToken cancellationToken) =>
        Task.FromResult(PasswordUsers.Contains(id));

    public Task UpsertSteamIdentityAsync(
        UserId id,
        string steamId64,
        string personaName,
        Instant fetchedAt,
        CancellationToken cancellationToken)
    {
        SteamIdentities.RemoveAll(identity => identity.Id == id);
        SteamIdentities.Add((id, steamId64, personaName, fetchedAt));
        return Task.CompletedTask;
    }

    public Task RemoveSteamIdentityAsync(UserId id, CancellationToken cancellationToken)
    {
        RemovedSteamIdentities.Add(id);
        SteamIdentities.RemoveAll(identity => identity.Id == id);
        return Task.CompletedTask;
    }

    public Task PurgeCredentialsAsync(UserId id, CancellationToken cancellationToken)
    {
        PurgedCredentials.Add(id);
        return Task.CompletedTask;
    }

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

    /// <summary>
    /// Optional ordered event trail shared with a <see cref="FakeUnitOfWork"/> so a test can pin
    /// A-016's commit-before-mail ordering instead of only counting messages.
    /// </summary>
    internal List<string>? Trail { get; set; }

    public Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        Trail?.Add("mail");
        return Task.CompletedTask;
    }

    public bool TryDispatch(EmailMessage message)
    {
        Messages.Add(message);
        Trail?.Add("mail");
        return true;
    }
}

internal sealed class FakeEmailRecipientReader : IEmailRecipientReader
{
    internal Dictionary<UserId, EmailRecipient> Recipients { get; } = [];

    internal List<IReadOnlyList<UserId>> Calls { get; } = [];

    public Task<IReadOnlyList<EmailRecipient>> GetAsync(
        IReadOnlyList<UserId> userIds,
        CancellationToken cancellationToken)
    {
        Calls.Add(userIds);
        List<EmailRecipient> results = [];
        foreach (UserId id in userIds)
        {
            if (Recipients.TryGetValue(id, out EmailRecipient? recipient))
            {
                results.Add(recipient);
            }
        }

        return Task.FromResult<IReadOnlyList<EmailRecipient>>(results);
    }
}

/// <summary>
/// D-189 §5: builds a real <see cref="InviteEmailService"/> over the fakes above, with an empty
/// recipient reader by default -- the safe no-op wiring for tests that exercise the five call sites
/// without asserting on email behavior. Tests that DO assert on dispatch construct their own reader
/// with recipients populated and pass it here.
/// </summary>
internal static class TestInviteEmailService
{
    internal static InviteEmailService Create(
        IEmailRecipientReader? reader = null,
        ISecurityTokenService? tokens = null,
        IEmailDispatcher? dispatcher = null,
        IUnitOfWork? unitOfWork = null,
        string publicBaseUrl = "https://partyup.social") =>
        new(
            reader ?? new FakeEmailRecipientReader(),
            tokens ?? new FakeTokenService(),
            dispatcher ?? new FakeEmailDispatcher(),
            unitOfWork ?? new FakeUnitOfWork(),
            new AccountEmailOptions(publicBaseUrl),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InviteEmailService>.Instance);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    internal int Executions { get; private set; }

    /// <summary>
    /// Optional ordered event trail shared with a <see cref="FakeEmailDispatcher"/> so a test can
    /// pin A-016's commit-before-mail ordering instead of only counting messages.
    /// </summary>
    internal List<string>? Trail { get; set; }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        Executions++;
        T result = await work(cancellationToken);
        Trail?.Add("commit");
        return result;
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

internal sealed class FakeSteamProfileClient : ISteamProfileClient
{
    internal SteamProfile? Profile { get; set; }

    public Task<SteamProfile?> GetProfileAsync(
        string steamId64,
        CancellationToken cancellationToken) =>
        Task.FromResult(Profile);
}

/// <summary>Never captures anything by default -- exactly the "capture failure never fails sign-in" case.</summary>
internal sealed class FakeAvatarCaptureService : IAvatarCaptureService
{
    internal AvatarCapture? Capture { get; set; }

    internal bool Throw { get; set; }

    public Task<AvatarCapture?> CaptureAsync(
        string upstreamUrl,
        AvatarSourceKind kind,
        CancellationToken cancellationToken) =>
        Throw
            ? throw new InvalidOperationException("Simulated capture failure.")
            : Task.FromResult(Capture);
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
