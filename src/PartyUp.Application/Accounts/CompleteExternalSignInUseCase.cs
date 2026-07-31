using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Auth;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Accounts;

public sealed class CompleteExternalSignInUseCase(
    IUserAccountGateway accounts,
    IUserRepository users,
    IFriendCodeGenerator friendCodes,
    IAccountSessionService sessions,
    ISecurityTokenService tokens,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    ISteamProfileClient steamProfiles,
    IClock clock)
{
    private static readonly Duration ReauthenticationTtl = Duration.FromMinutes(5);

    public async Task<ExternalSignInResult> ExecuteAsync(
        CompleteExternalSignInCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!ExternalProviderPolicy.IsSupported(command.Provider) ||
            string.IsNullOrWhiteSpace(command.ProviderKey))
        {
            return ExternalSignInResult.Failed("external_auth_failed");
        }

        string provider = ExternalProviderPolicy.Canonicalize(command.Provider);
        if (command.Mode == ExternalStartMode.Reauth)
        {
            return await CompleteReauthenticationAsync(
                command with { Provider = provider },
                cancellationToken);
        }

        if (command.Mode == ExternalStartMode.Link)
        {
            SteamProfileData? steamProfile = provider == ExternalProviderPolicy.Steam
                ? await ReadSteamProfileAsync(command.ProviderKey, cancellationToken)
                : null;
            return await CompleteLinkAsync(
                command with { Provider = provider },
                steamProfile,
                cancellationToken);
        }

        if (command.BoundUserId is not null)
        {
            return ExternalSignInResult.Failed("intent_mode_mismatch");
        }

        UserId? loginOwner = await accounts.FindUserIdByLoginAsync(
            provider,
            command.ProviderKey,
            cancellationToken);
        if (loginOwner is not null)
        {
            if (provider == ExternalProviderPolicy.Steam)
            {
                SteamProfileData profile = await ReadSteamProfileAsync(
                    command.ProviderKey,
                    cancellationToken);
                await unitOfWork.ExecuteAsync(
                    async ct =>
                    {
                        await UpsertSteamFactsAsync(loginOwner.Value, command.ProviderKey, profile, ct);
                        return true;
                    },
                    cancellationToken);
            }

            return ExternalSignInResult.SignedIn(loginOwner.Value);
        }

        if (provider == ExternalProviderPolicy.Google)
        {
            return await CompleteGoogleLoginAsync(command, cancellationToken);
        }

        SteamProfileData steam = await ReadSteamProfileAsync(
            command.ProviderKey,
            cancellationToken);
        return await CreateAccountAsync(command, email: null, steam, cancellationToken);
    }

    private async Task<ExternalSignInResult> CompleteReauthenticationAsync(
        CompleteExternalSignInCommand command,
        CancellationToken cancellationToken)
    {
        if (command.BoundUserId is not { } boundUserId ||
            command.CallerId != boundUserId)
        {
            return ExternalSignInResult.Failed("reauth_identity_mismatch");
        }

        UserId? loginOwner = await accounts.FindUserIdByLoginAsync(
            command.Provider,
            command.ProviderKey,
            cancellationToken);
        if (loginOwner != boundUserId)
        {
            return ExternalSignInResult.Failed("reauth_identity_mismatch");
        }

        IssuedToken proof = await unitOfWork.ExecuteAsync(
            ct => tokens.IssueAsync(
                new IssueTokenRequest(
                    AuthTokenPurpose.Reauthentication,
                    boundUserId,
                    null,
                    ReauthenticationTtl),
                ct),
            cancellationToken);
        return ExternalSignInResult.Reauthenticated(proof.RawToken);
    }

    private async Task<ExternalSignInResult> CompleteLinkAsync(
        CompleteExternalSignInCommand command,
        SteamProfileData? steamProfile,
        CancellationToken cancellationToken)
    {
        if (command.BoundUserId is not { } boundUserId ||
            command.CallerId != boundUserId)
        {
            return ExternalSignInResult.Failed("intent_mode_mismatch");
        }

        UserId? loginOwner = await accounts.FindUserIdByLoginAsync(
            command.Provider,
            command.ProviderKey,
            cancellationToken);
        if (loginOwner is not null && loginOwner != boundUserId)
        {
            return ExternalSignInResult.Failed("provider_already_linked");
        }

        if (loginOwner is null)
        {
            (bool Allowed, bool Added) mutation = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    UserId? appeared = await accounts.FindUserIdByLoginAsync(
                        command.Provider,
                        command.ProviderKey,
                        ct);
                    if (appeared is not null && appeared != boundUserId)
                    {
                        return (false, false);
                    }

                    if (appeared is null)
                    {
                        await accounts.AddLoginAsync(
                            boundUserId,
                            command.Provider,
                            command.ProviderKey,
                            ct);
                        if (steamProfile is not null)
                        {
                            await UpsertSteamFactsAsync(
                                boundUserId,
                                command.ProviderKey,
                                steamProfile,
                                ct);
                        }
                        await sessions.SignOutEverywhereAsync(boundUserId, ct);
                        return (true, true);
                    }

                    return (true, false);
                },
                cancellationToken);
            if (!mutation.Allowed)
            {
                return ExternalSignInResult.Failed("provider_already_linked");
            }

            if (mutation.Added)
            {
                await NotifyMethodAddedAsync(boundUserId, cancellationToken);
            }
        }

        return ExternalSignInResult.Linked;
    }

    private async Task<ExternalSignInResult> CompleteGoogleLoginAsync(
        CompleteExternalSignInCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.EmailVerified || string.IsNullOrWhiteSpace(command.Email))
        {
            return ExternalSignInResult.Failed("link_required");
        }

        string address;
        try
        {
            address = AccountInput.Email(command.Email);
        }
        catch (DomainException exception) when (exception.Error == DomainError.ValidationFailed)
        {
            return ExternalSignInResult.Failed("link_required");
        }

        string normalizedAddress = AccountInput.NormalizeEmail(address);
        UserId? emailOwner = await accounts.FindUserIdByEmailAsync(
            normalizedAddress,
            cancellationToken);
        if (emailOwner is not null)
        {
            AccountEmailInfo? localEmail = await accounts.GetEmailAsync(
                emailOwner.Value,
                cancellationToken);
            if (localEmail is not { IsConfirmed: true })
            {
                return ExternalSignInResult.Failed("link_required");
            }

            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    await accounts.AddLoginAsync(
                        emailOwner.Value,
                        ExternalProviderPolicy.Google,
                        command.ProviderKey,
                        ct);
                    await sessions.SignOutEverywhereAsync(emailOwner.Value, ct);
                    return true;
                },
                cancellationToken);
            await NotifyMethodAddedAsync(emailOwner.Value, cancellationToken);
            return ExternalSignInResult.SignedIn(emailOwner.Value);
        }

        return await CreateAccountAsync(command, address, steamProfile: null, cancellationToken);
    }

    private async Task<ExternalSignInResult> CreateAccountAsync(
        CompleteExternalSignInCommand command,
        string? email,
        SteamProfileData? steamProfile,
        CancellationToken cancellationToken)
    {
        if (!command.AgeGateAttested)
        {
            return ExternalSignInResult.Failed("age_gate_required");
        }

        if (email is null && !ExternalProviderPolicy.CanCreateWithoutAddress(command.Provider))
        {
            return ExternalSignInResult.Failed("link_required");
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    if (email is not null)
                    {
                        UserId? appeared = await accounts.FindUserIdByEmailAsync(
                            AccountInput.NormalizeEmail(email),
                            ct);
                        if (appeared is not null)
                        {
                            return ExternalSignInResult.Failed("email_unavailable");
                        }
                    }

                    UserId? loginOwner = await accounts.FindUserIdByLoginAsync(
                        command.Provider,
                        command.ProviderKey,
                        ct);
                    if (loginOwner is not null)
                    {
                        return ExternalSignInResult.SignedIn(loginOwner.Value);
                    }

                    UserId createdId = UserId.New();
                    FriendCode friendCode = await friendCodes.NextAsync(ct);
                    User user = User.BeginOnboarding(
                        createdId,
                        friendCode,
                        AgeGateSource.SelfAttested,
                        clock);
                    users.Add(user);
                    await accounts.CreateAccountAsync(
                        createdId,
                        email,
                        emailConfirmed: email is not null,
                        password: null,
                        ct);
                    await accounts.AddLoginAsync(
                        createdId,
                        command.Provider,
                        command.ProviderKey,
                        ct);
                    if (steamProfile is not null)
                    {
                        await UpsertSteamFactsAsync(
                            user,
                            command.ProviderKey,
                            steamProfile,
                            ct);
                    }
                    return ExternalSignInResult.SignedIn(createdId);
                },
                cancellationToken);
        }
        catch (DomainException exception) when (exception.Error == DomainError.Duplicate)
        {
            return ExternalSignInResult.Failed("email_unavailable");
        }
    }

    private async Task<SteamProfileData> ReadSteamProfileAsync(
        string steamId64,
        CancellationToken cancellationToken)
    {
        SteamProfile? profile = await steamProfiles.GetProfileAsync(steamId64, cancellationToken);
        string fallback = $"Steam {steamId64[^Math.Min(4, steamId64.Length)..]}";
        string displayName = string.IsNullOrWhiteSpace(profile?.DisplayName)
            ? fallback
            : profile.DisplayName.Trim();
        if (displayName.Length > GamingHandle.MaxLength)
        {
            displayName = displayName[..GamingHandle.MaxLength];
        }

        string? avatarUrl = profile?.AvatarUrl;
        if (avatarUrl?.Length > 2048)
        {
            avatarUrl = null;
        }

        return new SteamProfileData(displayName, avatarUrl);
    }

    private async Task UpsertSteamFactsAsync(
        UserId userId,
        string steamId64,
        SteamProfileData profile,
        CancellationToken cancellationToken)
    {
        User user = await users.FindForUpdateAsync(userId, cancellationToken)
            ?? throw new DomainException(DomainError.NotFound, "The user was not found.");
        await UpsertSteamFactsAsync(user, steamId64, profile, cancellationToken);
    }

    private async Task UpsertSteamFactsAsync(
        User user,
        string steamId64,
        SteamProfileData profile,
        CancellationToken cancellationToken)
    {
        PlatformLink? link = user.PlatformLinks.SingleOrDefault(
            value => value.Platform == Platform.Steam);
        GamingHandle handle = GamingHandle.Create(profile.PersonaName);
        if (link is null)
        {
            user.LinkPlatform(Platform.Steam, handle);
        }
        else
        {
            user.ChangePlatformHandle(link.Id, handle);
        }

        await accounts.UpsertSteamIdentityAsync(
            user.Id,
            steamId64,
            profile.PersonaName,
            profile.AvatarUrl,
            clock.GetCurrentInstant(),
            cancellationToken);
    }

    private async Task NotifyMethodAddedAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        AccountEmailInfo? email = await accounts.GetEmailAsync(userId, cancellationToken);
        if (email is not { IsConfirmed: true })
        {
            return;
        }

        Locale locale = (await users.FindAsync(userId, cancellationToken))?.Locale
            ?? AccountInput.Locale(null);
        await emailDispatcher.DispatchAsync(
            new EmailMessage(
                email.Email,
                locale,
                EmailTemplate.AuthMethodAdded,
                AccountInput.EmptyModel),
            cancellationToken);
    }
}

internal sealed record SteamProfileData(string PersonaName, string? AvatarUrl);

public sealed record CompleteExternalSignInCommand(
    string Provider,
    string ProviderKey,
    string? Email,
    bool EmailVerified,
    ExternalStartMode Mode,
    bool AgeGateAttested,
    UserId? BoundUserId,
    UserId? CallerId);

public sealed record ExternalSignInResult(
    bool Succeeded,
    UserId? UserId,
    bool ShouldSignIn,
    bool MethodLinked,
    string? ReauthenticationToken,
    string? Code)
{
    public static ExternalSignInResult Linked { get; } =
        new(true, null, false, true, null, null);

    public static ExternalSignInResult SignedIn(UserId userId) =>
        new(true, userId, true, false, null, null);

    public static ExternalSignInResult Reauthenticated(string token) =>
        new(true, null, false, false, token, null);

    public static ExternalSignInResult Failed(string code) =>
        new(false, null, false, false, null, code);
}
