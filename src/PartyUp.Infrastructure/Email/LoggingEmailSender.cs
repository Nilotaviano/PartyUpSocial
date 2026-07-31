using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PartyUp.Application.Email;

namespace PartyUp.Infrastructure.Email;

public sealed partial class LoggingEmailSender(
    IEmailTemplateRenderer renderer,
    LoggingEmailSenderEnvironment environment,
    IConfiguration configuration,
    ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment)
        {
            throw new InvalidOperationException(
                "The logging email sender is restricted to the Development environment.");
        }

        RenderedEmail rendered = renderer.Render(message);
        LogDevelopmentEmail(
            logger,
            message.Template,
            rendered.Subject,
            rendered.TextBody,
            rendered.HtmlBody);

        string dropPath = configuration["Email:DropPath"]
            ?? Path.Combine(environment.ContentRootPath, "mail-drop");
        try
        {
            Directory.CreateDirectory(dropPath);
            string fileName = $"{Guid.CreateVersion7():N}-{message.Template}.eml";
            string eml = $"""
                To: {SanitizeHeader(message.ToAddress)}
                From: PartyUp <no-reply@mail.partyup.social>
                Subject: {SanitizeHeader(rendered.Subject)}
                MIME-Version: 1.0
                Content-Type: multipart/alternative; boundary="partyup"

                --partyup
                Content-Type: text/plain; charset=utf-8

                {rendered.TextBody}
                --partyup
                Content-Type: text/html; charset=utf-8

                {rendered.HtmlBody}
                --partyup--
                """;
            await File.WriteAllTextAsync(
                Path.Combine(dropPath, fileName),
                eml,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            LogDropPathNotWritable(logger, exception);
        }
    }

    private static string SanitizeHeader(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Development email {Template}: {Subject}\n{TextBody}\n{HtmlBody}")]
    private static partial void LogDevelopmentEmail(
        ILogger logger,
        EmailTemplate template,
        string subject,
        string textBody,
        string htmlBody);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The development email drop path is not writable")]
    private static partial void LogDropPathNotWritable(ILogger logger, Exception exception);
}

public sealed record LoggingEmailSenderEnvironment(bool IsDevelopment, string ContentRootPath);