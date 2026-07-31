using PartyUp.Application.Email;
using PartyUp.Infrastructure.Email;

namespace PartyUp.Api.Hosting;

public sealed partial class EmailDispatchHostedService(
    ChannelEmailDispatcher dispatcher,
    IEmailSender sender,
    ILogger<EmailDispatchHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (EmailMessage message in dispatcher.ReadAllAsync(stoppingToken))
        {
            try
            {
                await sender.SendAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogDispatchFailure(logger, message.Template, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Email dispatch failed for template {Template}")]
    private static partial void LogDispatchFailure(
        ILogger logger,
        EmailTemplate template,
        Exception exception);
}