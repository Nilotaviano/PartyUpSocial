using PartyUp.Infrastructure.Jobs;

namespace PartyUp.Api.Hosting;

/// <summary>
/// The browser lane's spawned-process trigger seam (PHASE7_DESIGN.md §6): the black-box, out-of-process
/// Playwright fixture cannot reach Hangfire's in-process minutely cadence, so this <see cref="PeriodicTimer"/>
/// hosted service — the shipped <c>DeletionSweepHostedService</c> A-037 precedent — ticks
/// <see cref="PushDispatchJob"/> through its own scope on a much shorter interval. Registered only when
/// <c>Push:DispatchIntervalSeconds</c> is set; production never sets it and keeps the minutely Hangfire
/// registration in <see cref="RecurringJobRegistrationService"/> as the only cadence.
/// </summary>
public sealed partial class PushDispatchTriggerService(
    IServiceScopeFactory scopeFactory,
    TimeSpan interval,
    ILogger<PushDispatchTriggerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PushDispatchJob>().ExecuteAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogTickFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Push dispatch trigger tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
