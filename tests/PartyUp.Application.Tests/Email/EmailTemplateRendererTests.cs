using PartyUp.Application.Email;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Tests.Email;

public sealed class EmailTemplateRendererTests
{
    private readonly EmbeddedEmailTemplateRenderer _renderer = new();

    [Fact]
    public void Render_UsesTheRecipientLocale_D060()
    {
        foreach (EmailTemplate template in Enum.GetValues<EmailTemplate>())
        {
            foreach (string locale in new[] { "en", "pt-BR", "es" })
            {
                RenderedEmail rendered = _renderer.Render(CreateMessage(template, locale));
                Assert.False(string.IsNullOrWhiteSpace(rendered.Subject));
                Assert.False(string.IsNullOrWhiteSpace(rendered.TextBody));
                Assert.False(string.IsNullOrWhiteSpace(rendered.HtmlBody));
            }
        }

        RenderedEmail portuguese = _renderer.Render(
            CreateMessage(EmailTemplate.MagicLink, "pt-BR"));
        Assert.Equal("Seu link mágico do PartyUp", portuguese.Subject);
        Assert.Contains("&lt;Steam&gt;", _renderer.Render(
            CreateMessage(EmailTemplate.AuthMethodAdded, "en")).HtmlBody);
    }

    [Fact]
    public void Render_FallsBackToEnglish_D146()
    {
        RenderedEmail rendered = _renderer.Render(
            CreateMessage(EmailTemplate.MagicLink, "fr"));

        Assert.Equal("Your PartyUp magic link", rendered.Subject);
    }

    [Fact]
    public void Render_PutsTheTokenInTheFragment_D067()
    {
        foreach (EmailTemplate template in Enum.GetValues<EmailTemplate>())
        {
            RenderedEmail rendered = _renderer.Render(CreateMessage(template, "en"));
            string output = $"{rendered.Subject}\n{rendered.TextBody}\n{rendered.HtmlBody}";
            Assert.DoesNotContain("?token=", output, StringComparison.OrdinalIgnoreCase);

            if (output.Contains("raw-token", StringComparison.Ordinal))
            {
                Assert.Contains("#token=raw-token", output, StringComparison.Ordinal);
            }
        }
    }

    private static EmailMessage CreateMessage(EmailTemplate template, string locale) =>
        new(
            "recipient@example.test",
            Locale.Create(locale),
            template,
            new Dictionary<string, string>
            {
                ["PublicBaseUrl"] = "https://partyup.social",
                ["Token"] = "raw-token",
                ["GraceEndsAt"] = "August 7, 2026 at 12:00",
                ["NewEmail"] = "new@example.test",
                ["Method"] = "<Steam>",
            });
}