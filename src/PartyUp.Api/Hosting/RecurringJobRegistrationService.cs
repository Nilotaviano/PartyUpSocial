using Hangfire;
using PartyUp.Infrastructure.Jobs;

namespace PartyUp.Api.Hosting;

public sealed partial class RecurringJobRegistrationService(
    IServiceScopeFactory scopeFactory,
    ILogger<RecurringJobRegistrationService> logger) : BackgroundService
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
                recurringJobs.AddOrUpdate<SessionDueSweepJob>(
                    "session-due-sweep",
                    job => job.ExecuteAsync(CancellationToken.None),
                    Cron.Minutely);
                recurringJobs.AddOrUpdate<SessionReminderDueJob>(
                    "session-reminder-due",
                    job => job.ExecuteAsync(CancellationToken.None),
                    Cron.Minutely);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                TimeSpan delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                attempt++;
                LogRegistrationFailed(logger, delay, exception);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Recurring job registration failed; retrying in {Delay}")]
    private static partial void LogRegistrationFailed(
        ILogger logger,
        TimeSpan delay,
        Exception exception);
}
