using System.Text.RegularExpressions;
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

        foreach (string locale in new[] { "en", "pt-BR", "es" })
        {
            RenderedEmail confirmation = _renderer.Render(
                CreateMessage(EmailTemplate.DeletionRequestConfirmation, locale));
            Assert.Contains("#token=raw-token", confirmation.TextBody, StringComparison.Ordinal);

            RenderedEmail graceStarted = _renderer.Render(
                CreateMessage(EmailTemplate.DeletionRequested, locale));
            string graceOutput = $"{graceStarted.Subject}\n{graceStarted.TextBody}\n{graceStarted.HtmlBody}";
            Assert.Contains("localized-grace-end", graceOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-token", graceOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("account-deletion/confirm", graceOutput, StringComparison.Ordinal);
        }

        foreach (EmailTemplate template in new[]
        {
            EmailTemplate.SessionInvitation,
            EmailTemplate.PollInvitation,
            EmailTemplate.SessionJoinRequestApproved,
            EmailTemplate.PollJoinRequestApproved,
        })
        {
            RenderedEmail english = _renderer.Render(CreateMessage(template, "en"));
            foreach (string locale in new[] { "pt-BR", "es" })
            {
                RenderedEmail translated = _renderer.Render(CreateMessage(template, locale));
                Assert.NotEqual(english.Subject, translated.Subject);
                Assert.NotEqual(english.TextBody, translated.TextBody);
                Assert.NotEqual(english.HtmlBody, translated.HtmlBody);
            }

            Assert.Contains("Friday Game Night", english.Subject, StringComparison.Ordinal);
            Assert.Contains(
                "/email-unsubscribe#token=raw-token",
                english.TextBody,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_FallsBackToEnglish_D146()
    {
        RenderedEmail rendered = _renderer.Render(
            CreateMessage(EmailTemplate.MagicLink, "fr"));

        Assert.Equal("Your PartyUp magic link", rendered.Subject);
    }

    [Theory]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("PT-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("es-ES", "es")]
    [InlineData("EN-us", "en")]
    public void Render_MatchesLocaleCaseAndLanguageSubtags_D146(
        string requested,
        string expected)
    {
        Assert.Equal(
            expected,
            EmbeddedEmailTemplateRenderer.CanonicalizeLocale(requested));
        RenderedEmail rendered = _renderer.Render(
            CreateMessage(EmailTemplate.MagicLink, requested));
        Assert.False(string.IsNullOrWhiteSpace(rendered.Subject));
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

    [Theory]
    [InlineData(EmailTemplate.EmailVerification)]
    [InlineData(EmailTemplate.MagicLink)]
    public void Render_PutsCanonicalContinuationInTheFragmentForEveryLocale_D031(
        EmailTemplate template)
    {
        foreach (string locale in new[] { "en", "pt-BR", "es" })
        {
            var message = new EmailMessage(
                "recipient@example.test",
                Locale.Create(locale),
                template,
                PartyUp.Application.Accounts.AccountInput.LinkModel(
                    "https://partyup.social",
                    "raw-token",
                    "/add/23456789AB"));

            RenderedEmail rendered = _renderer.Render(message);
            Assert.Contains(
                "#token=raw-token&next=%2Fadd%2F23456789AB",
                rendered.TextBody,
                StringComparison.Ordinal);
            Assert.Contains(
                "#token=raw-token&amp;next=%2Fadd%2F23456789AB",
                rendered.HtmlBody,
                StringComparison.Ordinal);
            Assert.DoesNotContain("?next=", rendered.TextBody, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_StripsCrlfFromASubstitutedSubjectValue_D189()
    {
        var message = new EmailMessage(
            "recipient@example.test",
            Locale.Create("en"),
            EmailTemplate.SessionInvitation,
            new Dictionary<string, string>
            {
                ["PublicBaseUrl"] = "https://partyup.social",
                ["Token"] = "raw-token",
                ["RecipientName"] = "Recipient",
                ["OrganizerName"] = "Organizer",
                ["Title"] = "Friday Game Night\r\nX-Injected-Header: evil",
                ["Notes"] = string.Empty,
                ["Link"] = "https://partyup.social/sessions/00000000-0000-0000-0000-000000000000",
                ["StartTime"] = "localized-start-time",
            });

        RenderedEmail rendered = _renderer.Render(message);

        Assert.DoesNotContain('\r', rendered.Subject);
        Assert.DoesNotContain('\n', rendered.Subject);
        Assert.Contains("Friday Game NightX-Injected-Header: evil", rendered.Subject, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EmailTemplate.SessionInvitation)]
    [InlineData(EmailTemplate.PollInvitation)]
    [InlineData(EmailTemplate.SessionJoinRequestApproved)]
    [InlineData(EmailTemplate.PollJoinRequestApproved)]
    public void Render_NeverSubstitutesUserContentInsideAnHref_D189(EmailTemplate template)
    {
        const string notesWithHrefLikeContent = "\">Click me</a><a href=\"https://evil.example.test";
        var message = new EmailMessage(
            "recipient@example.test",
            Locale.Create("en"),
            template,
            new Dictionary<string, string>
            {
                ["PublicBaseUrl"] = "https://partyup.social",
                ["Token"] = "raw-token",
                ["RecipientName"] = "Recipient",
                ["OrganizerName"] = "Organizer",
                ["Title"] = "Friday Game Night",
                ["Notes"] = notesWithHrefLikeContent,
                ["Link"] = "https://partyup.social/sessions/00000000-0000-0000-0000-000000000000",
                ["StartTime"] = "localized-start-time",
            });

        RenderedEmail rendered = _renderer.Render(message);

        Assert.DoesNotContain(
            $"href=\"{notesWithHrefLikeContent}",
            rendered.HtmlBody,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(EmailTemplate.SessionInvitation)]
    [InlineData(EmailTemplate.PollInvitation)]
    [InlineData(EmailTemplate.SessionJoinRequestApproved)]
    [InlineData(EmailTemplate.PollJoinRequestApproved)]
    public void HtmlTemplateHrefsOnlyEverInterpolateServerBuiltUrls_D189(EmailTemplate template)
    {
        // Link is server-built in InviteEmailService (PublicBaseUrl + segment + entity GUID),
        // so it is the only non-token placeholder an href may carry.
        string[] allowed = ["PublicBaseUrl", "Token", "Link"];

        foreach (string locale in new[] { "en", "pt-BR", "es" })
        {
            string html = ReadTemplateResource(template, locale, "html");
            MatchCollection hrefs = Regex.Matches(
                html,
                "href=\"(?<url>[^\"]*)\"",
                RegexOptions.CultureInvariant);

            Assert.NotEmpty(hrefs);
            foreach (Match href in hrefs)
            {
                string url = href.Groups["url"].Value;
                Assert.True(
                    url.StartsWith("{{PublicBaseUrl}}", StringComparison.Ordinal) ||
                    url.StartsWith("{{Link}}", StringComparison.Ordinal),
                    $"{template}.{locale}.html has an href that is not rooted in a server-built URL: {url}");

                foreach (Match placeholder in Regex.Matches(
                    url,
                    @"\{\{(?<key>[A-Za-z][A-Za-z0-9]*)\}\}",
                    RegexOptions.CultureInvariant))
                {
                    string key = placeholder.Groups["key"].Value;
                    Assert.True(
                        allowed.Contains(key, StringComparer.Ordinal),
                        $"{template}.{locale}.html interpolates '{key}' inside an href.");
                }
            }
        }
    }

    private static string ReadTemplateResource(
        EmailTemplate template,
        string locale,
        string part)
    {
        string resourceName = $"PartyUp.Application.Email.Templates.{template}.{locale}.{part}";
        using Stream stream = typeof(EmbeddedEmailTemplateRenderer).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing resource '{resourceName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
                ["Continuation"] = string.Empty,
                ["GraceEndsAt"] = "localized-grace-end",
                ["NewEmail"] = "new@example.test",
                ["Method"] = "<Steam>",
                ["RecipientName"] = "Recipient",
                ["OrganizerName"] = "Organizer",
                ["Title"] = "Friday Game Night",
                ["Notes"] = string.Empty,
                ["Link"] = "https://partyup.social/sessions/00000000-0000-0000-0000-000000000000",
                ["StartTime"] = "localized-start-time",
            });
}
