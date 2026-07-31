using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace PartyUp.Application.Email;

public sealed partial class EmbeddedEmailTemplateRenderer : IEmailTemplateRenderer
{
    private const string FallbackLocale = "en";
    private static readonly Assembly TemplateAssembly =
        typeof(EmbeddedEmailTemplateRenderer).Assembly;

    public RenderedEmail Render(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        string locale = IsSupported(message.Locale.Value)
            ? message.Locale.Value
            : FallbackLocale;

        string subject = Substitute(
            ReadResource(message.Template, locale, "subject.txt"),
            message.Model,
            htmlEncode: false);
        string text = Substitute(
            ReadResource(message.Template, locale, "text"),
            message.Model,
            htmlEncode: false);
        string html = Substitute(
            ReadResource(message.Template, locale, "html"),
            message.Model,
            htmlEncode: true);

        return new RenderedEmail(subject.Trim(), text.Trim(), html.Trim());
    }

    private static bool IsSupported(string locale) =>
        locale is "en" or "pt-BR" or "es";

    private static string ReadResource(EmailTemplate template, string locale, string part)
    {
        string resourceName =
            $"{TemplateAssembly.GetName().Name}.Email.Templates.{template}.{locale}.{part}";
        using Stream stream = TemplateAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded email template resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Substitute(
        string source,
        IReadOnlyDictionary<string, string> model,
        bool htmlEncode)
    {
        return PlaceholderPattern().Replace(source, match =>
        {
            string key = match.Groups["key"].Value;
            if (!model.TryGetValue(key, out string? value))
            {
                throw new InvalidOperationException(
                    $"Email template placeholder '{key}' has no model value.");
            }

            return htmlEncode ? HtmlEncoder.Default.Encode(value) : value;
        });
    }

    [GeneratedRegex(@"\{\{(?<key>[A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}