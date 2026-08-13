using System.Globalization;
using NodaTime;
using PartyUp.Application.Notifications;
using PartyUp.Application.Push;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Application.Tests.Push;

/// <summary>
/// PHASE8_DESIGN.md §3 S6's "align email vs UI datetime shapes" duty: the server-rendered instant
/// (<see cref="LocalizedInstantFormatter"/>, reached here through <see cref="PushPayloadFactory"/>)
/// must use the same short-date/short-time shape across locales, not the long-date "f" format.
/// </summary>
public sealed class PushPayloadFactoryLocalizedInstantTests
{
    [Theory]
    [InlineData("en", "en-US")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("es", "es-ES")]
    public void Build_FormatsOptionStartAsShortDateShortTime_D060(string locale, string cultureName)
    {
        var factory = new PushPayloadFactory(new EchoOptionStartRenderer());
        var localTime = new DateTime(2026, 8, 14, 19, 0, 0);
        var row = new NotificationReadModel(
            NotificationId.New(),
            NotificationType.PollOptionSuggested,
            Instant.FromUtc(2026, 8, 14, 23, 0),
            null,
            null,
            null,
            NotificationSubjectKind.None,
            null,
            null,
            Instant.FromUtc(2026, 8, 14, 23, 0),
            locale,
            DateTimeZoneProviders.Tzdb["America/New_York"]);

        PushPayload payload = factory.Build(row);

        Assert.Equal(localTime.ToString("g", CultureInfo.GetCultureInfo(cultureName)), payload.Body);
    }

    private sealed class EchoOptionStartRenderer : IPushTemplateRenderer
    {
        public (string Title, string Body) Render(
            NotificationType type,
            string locale,
            IReadOnlyDictionary<string, string> model) => (string.Empty, model["OptionStart"]);
    }
}
