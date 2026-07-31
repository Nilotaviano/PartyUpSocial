namespace PartyUp.Application.Email;

public interface IEmailTemplateRenderer
{
    RenderedEmail Render(EmailMessage message);
}

public sealed record RenderedEmail(string Subject, string TextBody, string HtmlBody);