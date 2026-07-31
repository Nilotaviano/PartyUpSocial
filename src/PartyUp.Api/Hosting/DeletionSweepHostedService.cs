using System.Threading.Channels;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.Hosting;

public sealed partial class DeletionSweepHostedService(
    IServiceScopeFactory scopeFactory,
    DeletionSweepOptions options,
    AuthTokenRetentionOptions retention,
    ILogger<DeletionSweepHostedService> logger)
    : BackgroundService, IDeletionExecutionScheduler
{
    private readonly Channel<UserId> _targeted = Channel.CreateUnbounded<UserId>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ValueTask ScheduleAsync(UserId id, CancellationToken cancellationToken) =>
        _targeted.Writer.WriteAsync(id, cancellationToken);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task targeted = RunTargetedAsync(stoppingToken);
        if (!options.Enabled && retention.Retention <= NodaTime.Duration.Zero)
        {
            return targeted;
        }

        return Task.WhenAll(targeted, RunPeriodicAsync(stoppingToken));
    }

    private async Task RunTargetedAsync(CancellationToken cancellationToken)
    {
        await foreach (UserId id in _targeted.Reader.ReadAllAsync(cancellationToken))
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IDeletionUserExecutor executor =
                scope.ServiceProvider.GetRequiredService<IDeletionUserExecutor>();
            try
            {
                await executor.ExecuteAsync(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTargetedFailure(logger, id.Value, exception);
            }
        }
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await RunTickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTickFailure(logger, exception);
            }
        }
    }

    private async Task RunTickAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        await services.GetRequiredService<IDeletionJournalShipper>()
            .ShipPendingAsync(cancellationToken);
        int purged = await services.GetRequiredService<PurgeExpiredAuthTokensUseCase>()
            .ExecuteAsync(cancellationToken);
        LogTokensPurged(logger, purged);

        if (options.Enabled)
        {
            DeletionSweepResult result = await services
                .GetRequiredService<ExecuteDueDeletionsUseCase>()
                .ExecuteAsync(options.BatchSize, cancellationToken);
            LogSweepCompleted(logger, result.Executed, result.Failed);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Targeted deletion execution failed for user {UserId}")]
    private static partial void LogTargetedFailure(
        ILogger logger,
        Guid userId,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deletion sweep tick failed")]
    private static partial void LogTickFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} retained auth tokens")]
    private static partial void LogTokensPurged(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Deletion sweep completed: {Executed} executed, {Failed} failed")]
    private static partial void LogSweepCompleted(ILogger logger, int executed, int failed);
}

public sealed partial class ScopedDeletionUserExecutor(
    IServiceScopeFactory scopeFactory,
    ILogger<ScopedDeletionUserExecutor> logger) : IDeletionUserExecutor
{
    public async Task<bool> ExecuteAsync(UserId id, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            return await scope.ServiceProvider
                .GetRequiredService<ExecuteDeletionForUserUseCase>()
                .ExecuteAsync(id, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogUserFailure(logger, id.Value, exception);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Deletion execution failed for user {UserId}")]
    private static partial void LogUserFailure(
        ILogger logger,
        Guid userId,
        Exception exception);
}

public sealed record DeletionSweepOptions(bool Enabled, TimeSpan Interval, int BatchSize);
