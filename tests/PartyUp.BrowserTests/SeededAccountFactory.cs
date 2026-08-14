using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Onboarding;
using PartyUp.Application.Profile;
using PartyUp.Domain.Kernel;
using PartyUp.Infrastructure;

namespace PartyUp.BrowserTests;

/// <summary>
/// Creates fully-registered, onboarded, tour-completed accounts for the browser lane WITHOUT
/// driving the registration UI.
/// <para>
/// Every UI registration costs <see cref="PlaywrightFixture"/>'s 65-second shared-rate-window
/// barrier plus a multi-page mailbox round trip, and the overwhelming majority of the lane's
/// accounts are only ever *props* — an organizer to create a session, a friend to invite, a viewer
/// to read a Discover row. None of those facts assert anything about registration or onboarding, so
/// paying the barrier for them bought nothing but wall-clock. The register/verify/onboard UI itself
/// stays covered by the call sites that genuinely audit it
/// (<see cref="Tour.TourFlowTests"/>, <see cref="Accessibility.AxeAuditTests"/>'s onboarding-step
/// capture), which still go through <see cref="PlaywrightFixture.RegisterAndOnboardAsync"/>.
/// </para>
/// <para>
/// The mechanism is the real Application layer, not hand-written SQL: this composes a minimal
/// <see cref="ServiceProvider"/> over the same connection string the running API was handed
/// (<c>AddInfrastructure</c> plus the handful of use cases <c>Program.cs</c> registers itself) and
/// drives <see cref="VerifyEmailUseCase"/>, <see cref="CompleteOnboardingUseCase"/> and
/// <see cref="CompleteTourUseCase"/>. Password hashing, username reservation, friend-code
/// allocation, lifecycle transitions and every materialized column therefore come from the shipped
/// domain code — the only thing skipped is HTTP.
/// </para>
/// <para>
/// It is a *second* process reading the same database as the API under test (the lane is black box
/// by design, A-082), which is exactly the standing of this fixture's other DB backdoors
/// (<c>MintAuthTokenAsync</c>, <c>GrantAdminRoleAsync</c>) — the difference is that this one writes
/// through the domain instead of around it.
/// </para>
/// </summary>
internal sealed class SeededAccountFactory : IAsyncDisposable
{
    /// <summary>
    /// Long enough to clear <c>AddIdentityCore</c>'s 12-character minimum
    /// (<c>DependencyInjection.cs</c>) — the seeded accounts never sign in with it (they arrive
    /// through a minted magic-link token), it exists so the account is a genuine password account
    /// rather than a password-less shape no registration path can produce.
    /// </summary>
    public const string Password = "correct horse battery staple 42";

    private readonly ServiceProvider _services;

    public SeededAccountFactory(string connectionString, string publicBaseUrl)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Db"] = connectionString,
                ["PublicBaseUrl"] = publicBaseUrl,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        // Registered by Program.cs rather than AddInfrastructure, so this composition adds the
        // same ones it does (see that file's own service registrations).
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<SeededUserAccessor>();
        services.AddScoped<ICurrentUserAccessor>(provider =>
            provider.GetRequiredService<SeededUserAccessor>());
        services.AddScoped<VerifyEmailUseCase>();
        services.AddScoped<ValidateProfileDraftUseCase>();
        services.AddScoped<CompleteOnboardingUseCase>();
        services.AddScoped<CompleteTourUseCase>();

        _services = services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs the whole account lifecycle a UI registration would: issue the email-verification token
    /// <see cref="RegisterWithPasswordUseCase"/> would have mailed, redeem it (which is what creates
    /// the <c>users</c> row, the friend code and the Identity account), complete onboarding with the
    /// supplied profile, then mark the first-login tour done — the fixture's UI registration skips
    /// the tour explicitly too, so a seeded account leaves no overlay for the caller's first
    /// assertion to trip over.
    /// </summary>
    public async Task<Guid> CreateAsync(
        string email,
        string username,
        string displayName,
        string timeZoneId,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail = AccountInput.NormalizeEmail(email);

        string rawToken;
        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            var tokens = scope.ServiceProvider.GetRequiredService<ISecurityTokenService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            IssuedToken issued = await unitOfWork.ExecuteAsync(
                ct => tokens.IssueAsync(
                    new IssueTokenRequest(
                        AuthTokenPurpose.EmailVerification,
                        null,
                        normalizedEmail,
                        Duration.FromHours(1),
                        new PendingRegistration("en", true),
                        Email: email),
                    ct),
                cancellationToken);
            rawToken = issued.RawToken;
        }

        Guid userId;
        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            var verify = scope.ServiceProvider.GetRequiredService<VerifyEmailUseCase>();
            EmailVerificationResult verified = await verify.ExecuteAsync(
                rawToken,
                Password,
                ageGateAttested: true,
                cancellationToken);
            if (!verified.Succeeded || verified.UserId is not { } created)
            {
                throw new InvalidOperationException(
                    $"Seeding '{username}' failed at email verification: " +
                    $"succeeded={verified.Succeeded}, code={verified.Code}, " +
                    $"alreadyExisted={verified.AccountAlreadyExisted}.");
            }

            userId = created.Value;
        }

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<SeededUserAccessor>().Current = new CurrentUser(
                UserId.From(userId),
                UserLifecycle.Onboarding,
                IsBanned: false,
                GraceEndsAt: null,
                IsAdmin: false,
                IsProfileComplete: false,
                Locale: null);
            var onboarding = scope.ServiceProvider.GetRequiredService<CompleteOnboardingUseCase>();
            OnboardingCompletionResult completed = await onboarding.ExecuteAsync(
                new ProfileDraft(
                    username,
                    displayName,
                    // The onboarding wizard's avatar step is a pick from the closed catalog and
                    // every lane account walked it with its default selection; the catalog's own
                    // first glyph is that same shape, resolved from the shipped catalog rather
                    // than hardcoded here.
                    new AvatarDraft("emoji", AvatarCatalog.Glyphs[0], null),
                    timeZoneId,
                    "en"),
                cancellationToken);
            if (!completed.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Seeding '{username}' failed at onboarding completion: code={completed.Code}, " +
                    $"errors={string.Join("; ", completed.Errors.Select(pair => pair.Key + "=" + string.Join(",", pair.Value)))}.");
            }
        }

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            var tour = scope.ServiceProvider.GetRequiredService<CompleteTourUseCase>();
            ProfileMutationResult result = await tour.ExecuteAsync(
                UserId.From(userId),
                cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Seeding '{username}' failed at tour completion: code={result.Code}.");
            }
        }

        return userId;
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    /// <summary>
    /// The API resolves the caller from the request's cookie; there is no request here, so the
    /// seeding scope states the subject directly. <see cref="CompleteOnboardingUseCase"/> reads
    /// nothing but <see cref="CurrentUser.Id"/> off it.
    /// </summary>
    private sealed class SeededUserAccessor : ICurrentUserAccessor
    {
        public CurrentUser? Current { get; set; }

        public Task<CurrentUser?> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }
}
