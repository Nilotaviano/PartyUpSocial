using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Push;

/// <summary>The embedded per-locale text half of §3.1's payload — title and body, before tag/url are attached.</summary>
public interface IPushTemplateRenderer
{
    (string Title, string Body) Render(
        NotificationType type,
        string locale,
        IReadOnlyDictionary<string, string> model);
}
