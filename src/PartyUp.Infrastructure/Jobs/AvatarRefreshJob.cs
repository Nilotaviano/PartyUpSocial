using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Jobs;

/// <summary>
/// AVATARS_DESIGN.md §3.4: the daily Steam avatar refresh, re-reading each <c>SteamPlatform</c>
/// <see cref="AvatarSource"/> from its owner's steamId64 via <see cref="ISteamProfileClient"/>,
/// staleest first, bounded to <see cref="AvatarRefreshOptions.BatchSize"/> rows per run (the
/// <c>DeletionSweepOptions</c> idiom). Google is capture-time only (§4) and has no refresh job.
/// </summary>
public sealed partial class AvatarRefreshJob(
    AppDbContext context,
    IUserRepository users,
    IUnitOfWork unitOfWork,
    ISteamProfileClient steamProfiles,
    IAvatarCaptureService avatarCapture,
    IClock clock,
    AvatarRefreshOptions options,
    ILogger<AvatarRefreshJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 55)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        List<Candidate> candidates = await context.AvatarSources
            .Where(source => source.Kind == AvatarSourceKind.SteamPlatform)
            .Join(
                context.SteamIdentities,
                source => source.UserId,
                identity => identity.UserId,
                (source, identity) => new Candidate(source.UserId, identity.SteamId64, source.FetchedAt))
            .OrderBy(candidate => candidate.FetchedAt)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (Candidate candidate in candidates)
        {
            try
            {
                await RefreshAsync(candidate, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A poison candidate (Steam outage, a decode failure) must not block the rest of the
                // batch -- contain per row and let the next daily run retry, same as PushDispatchJob.
                LogRefreshFailed(logger, candidate.UserId.Value, exception.GetType().Name);
                await TryStampAttemptAsync(candidate.UserId, cancellationToken);
            }
        }
    }

    private async Task RefreshAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        SteamProfile? profile = await steamProfiles.GetProfileAsync(candidate.SteamId64, cancellationToken);
        AvatarCapture? capture = string.IsNullOrWhiteSpace(profile?.AvatarUrl)
            ? null
            : await avatarCapture.CaptureAsync(profile.AvatarUrl, AvatarSourceKind.SteamPlatform, cancellationToken);

        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(candidate.UserId, ct);
                // The user may have unlinked Steam (which also removes the avatar source, D-050)
                // between this batch's candidate scan and this row's lock -- re-check inside the lock
                // so a captured picture never resurrects for a platform link that is gone.
                bool stillLinked = user?.PlatformLinks.Any(link => link.Platform == Platform.Steam) ?? false;
                if (!stillLinked)
                {
                    return true;
                }

                if (capture is not null)
                {
                    user!.CaptureAvatarSource(AvatarSourceKind.SteamPlatform, capture.Bytes, capture.ContentType, clock);
                }
                else
                {
                    // No picture (Steam has none) or the capture pipeline rejected it -- stamp the
                    // attempt anyway so this source rotates to the back of the staleest-first queue
                    // rather than starving every other user's refresh (AVATARS_DESIGN.md §3.4).
                    user!.MarkAvatarRefreshAttempt(AvatarSourceKind.SteamPlatform, clock);
                }

                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Best-effort: a candidate that threw before reaching <see cref="RefreshAsync"/>'s own stamping
    /// (a decode exception, an unexpected client error) still must not retry ahead of every other
    /// candidate on tomorrow's run. Swallows its own failures -- the batch's per-row containment
    /// already logged the original exception.
    /// </summary>
    private async Task TryStampAttemptAsync(UserId userId, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    User? user = await users.FindForUpdateAsync(userId, ct);
                    user?.MarkAvatarRefreshAttempt(AvatarSourceKind.SteamPlatform, clock);
                    return true;
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefreshFailed(logger, userId.Value, exception.GetType().Name);
        }
    }

    private sealed record Candidate(UserId UserId, string SteamId64, Instant FetchedAt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Avatar refresh for user {UserId} failed unexpectedly ({ExceptionType})")]
    private static partial void LogRefreshFailed(ILogger logger, Guid userId, string exceptionType);
}

/// <summary>The <c>DeletionSweepOptions</c> idiom's shape for the refresh job's per-run cap.</summary>
public sealed record AvatarRefreshOptions(int BatchSize);
