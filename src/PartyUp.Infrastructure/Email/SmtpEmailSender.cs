using System.Security.Cryptography;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using PartyUp.Application.Email;

namespace PartyUp.Infrastructure.Email;

public sealed partial class SmtpEmailSender : IEmailSender
{
    private const int SendAttempts = 3;
    private readonly IEmailTemplateRenderer _renderer;
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;

    public SmtpEmailSender(
        IEmailTemplateRenderer renderer,
        IConfiguration configuration,
        ILogger<SmtpEmailSender> logger)
    {
        _renderer = renderer;
        _logger = logger;
        _host = Required(configuration, "Email:Smtp:Host");
        _port = configuration.GetValue("Email:Smtp:Port", 587);
        _user = Required(configuration, "Email:Smtp:User");
        _password = Required(configuration, "Email:Smtp:Password");
        _fromAddress = configuration["Email:FromAddress"]
            ?? "no-reply@mail.partyup.social";
        _fromName = configuration["Email:FromName"] ?? "PartyUp";

        if (_port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Email:Smtp:Port must be between 1 and 65535.");
        }
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        RenderedEmail rendered = _renderer.Render(message);
        MimeMessage mimeMessage = BuildMessage(message, rendered);

        for (int attempt = 1; attempt <= SendAttempts; attempt++)
        {
            try
            {
                using var client = new SmtpClient();
                await client.ConnectAsync(
                    _host,
                    _port,
                    SecureSocketOptions.StartTls,
                    cancellationToken);
                await client.AuthenticateAsync(_user, _password, cancellationToken);
                await client.SendAsync(mimeMessage, cancellationToken);
                await client.DisconnectAsync(true, cancellationToken);
                return;
            }
            catch (Exception exception) when (
                attempt < SendAttempts && IsTransient(exception, cancellationToken))
            {
                int delayMilliseconds = (200 * (1 << (attempt - 1))) +
                    Random.Shared.Next(25, 151);
                LogTransientFailure(
                    _logger,
                    attempt,
                    message.Template,
                    RecipientHash(message.ToAddress),
                    exception);
                await Task.Delay(delayMilliseconds, cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                LogPermanentFailure(
                    _logger,
                    message.Template,
                    RecipientHash(message.ToAddress),
                    exception);
                return;
            }
        }
    }

    private MimeMessage BuildMessage(EmailMessage message, RenderedEmail rendered)
    {
        var result = new MimeMessage();
        result.From.Add(new MailboxAddress(_fromName, _fromAddress));
        result.Sender = new MailboxAddress(_fromName, _fromAddress);
        result.To.Add(MailboxAddress.Parse(message.ToAddress));
        result.Subject = rendered.Subject;
        result.Body = new BodyBuilder
        {
            TextBody = rendered.TextBody,
            HtmlBody = rendered.HtmlBody,
        }.ToMessageBody();
        return result;
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        exception switch
        {
            SmtpCommandException command =>
                (int)command.StatusCode is >= 400 and < 500,
            SmtpProtocolException => true,
            IOException => true,
            TimeoutException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            _ => false,
        };

    private static string Required(IConfiguration configuration, string key)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"{key} is required when Email:Provider is smtp.")
            : value;
    }

    private static string RecipientHash(string address) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            address.Trim().ToUpperInvariant())))[..12];

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Transient SMTP failure on attempt {Attempt} for {Template} recipient {RecipientHash}")]
    private static partial void LogTransientFailure(
        ILogger logger,
        int attempt,
        EmailTemplate template,
        string recipientHash,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "SMTP delivery failed for {Template} recipient {RecipientHash}")]
    private static partial void LogPermanentFailure(
        ILogger logger,
        EmailTemplate template,
        string recipientHash,
        Exception exception);
}
