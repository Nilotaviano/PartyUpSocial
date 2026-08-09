using System.Reflection;
using System.Text.RegularExpressions;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Push;

/// <summary>
/// Reads title/body text from embedded resources keyed
/// <c>PartyUp.Application.Push.Templates.{NotificationType}.{locale}.{part}</c> (PHASE7_DESIGN.md §3.1),
/// on the shipped <c>EmbeddedEmailTemplateRenderer</c> idiom (<c>:17,:35</c>) — locale is expected
/// already canonicalized by the caller (<see cref="Notifications.NotificationReadModel.RecipientLocale"/>
/// already runs through <c>EmbeddedEmailTemplateRenderer.CanonicalizeLocale</c>).
/// </summary>
public sealed partial class EmbeddedPushTemplateRenderer : IPushTemplateRenderer
{
    private static readonly Assembly TemplateAssembly = typeof(EmbeddedPushTemplateRenderer).Assembly;

    public (string Title, string Body) Render(
        NotificationType type,
        string locale,
        IReadOnlyDictionary<string, string> model)
    {
        ArgumentNullException.ThrowIfNull(model);

        string title = Substitute(ReadResource(type, locale, "title.txt"), model);
        string body = Substitute(ReadResource(type, locale, "body.txt"), model);
        return (title.Trim(), body.Trim());
    }

    private static string ReadResource(NotificationType type, string locale, string part)
    {
        string resourceName = $"{TemplateAssembly.GetName().Name}.Push.Templates.{type}.{locale}.{part}";
        using Stream stream = TemplateAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded push template resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Substitute(string source, IReadOnlyDictionary<string, string> model)
    {
        return PlaceholderPattern().Replace(source, match =>
        {
            string key = match.Groups["key"].Value;
            if (!model.TryGetValue(key, out string? value))
            {
                throw new InvalidOperationException(
                    $"Push template placeholder '{key}' has no model value.");
            }

            return value;
        });
    }

    [GeneratedRegex(@"\{\{(?<key>[A-Za-z][A-Za-z0-9]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
