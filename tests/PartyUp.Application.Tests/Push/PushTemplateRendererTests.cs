using PartyUp.Application.Push;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Tests.Push;

/// <summary>
/// PHASE7_DESIGN.md §3.1/§7 S3's closed template inventory gate: every <see cref="NotificationType"/>
/// member, crossed with every shipped locale and both parts, resolves through the embedded renderer —
/// greppable and closed because it enumerates the enum rather than a hand-maintained list.
/// </summary>
public sealed class PushTemplateRendererTests
{
    private static readonly string[] Locales = ["en", "pt-BR", "es"];

    private readonly EmbeddedPushTemplateRenderer _renderer = new();

    [Fact]
    public void Render_ResolvesEveryNotificationTypeAcrossEveryLocale_D112()
    {
        foreach (NotificationType type in Enum.GetValues<NotificationType>())
        {
            foreach (string locale in Locales)
            {
                (string title, string body) = _renderer.Render(type, locale, FullModel());

                Assert.False(string.IsNullOrWhiteSpace(title));
                Assert.False(string.IsNullOrWhiteSpace(body));
            }
        }
    }

    [Fact]
    public void Render_MissingModelValueThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => _renderer.Render(NotificationType.SessionConfirmed, "en", new Dictionary<string, string>()));
    }

    [Fact]
    public void Render_UnknownLocaleTemplateFileThrows()
    {
        Assert.Throws<InvalidOperationException>(
            () => _renderer.Render(NotificationType.SessionConfirmed, "fr", FullModel()));
    }

    /// <summary>
    /// A superset of every placeholder any single template uses — <c>PushPayloadFactory</c> only ever
    /// supplies the subset a given type's row actually carries, but this inventory gate exists to prove
    /// every template *resolves*, not to reproduce that per-type carry logic.
    /// </summary>
    private static Dictionary<string, string> FullModel() => new()
    {
        ["SubjectTitle"] = "Board Game Night",
        ["OptionStart"] = "Friday, August 14, 2026 7:00 PM",
        ["ActorName"] = "Alex",
    };
}
