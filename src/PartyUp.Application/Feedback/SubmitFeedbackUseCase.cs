using System.Globalization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Email;
using PartyUp.Domain.Feedback;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Feedback;

/// <param name="AdminRecipients">
/// D-215's destinations, read from <c>Admin:SeedEmails</c> — the same configured addresses
/// <c>AdminSeeder</c> grants the admin role to. Empty (no configured admins) simply sends nothing;
/// the row still commits.
/// </param>
public sealed record FeedbackEmailOptions(IReadOnlyList<string> AdminRecipients);

public sealed record SubmitFeedbackCommand(
    UserId? SubmitterUserId,
    FeedbackType Type,
    string Message,
    string? Route,
    string? UserAgent,
    string? Locale,
    string? AppVersion);

/// <summary>
/// D-212/D-213/D-215. The row commits first and alone; the admin notification is a best-effort,
/// post-commit enqueue (A-016) that cannot fail the request — a full or unavailable dispatch channel
/// drops the mail and leaves the crash-safe <c>feedback_items</c> row standing.
/// </summary>
public sealed class SubmitFeedbackUseCase(
    IFeedbackRepository feedback,
    IUnitOfWork unitOfWork,
    IEmailDispatcher emailDispatcher,
    FeedbackEmailOptions options,
    IClock clock)
{
    private static readonly Locale AdminLocale = Locale.Create("en");

    public async Task<FeedbackItemId> ExecuteAsync(
        SubmitFeedbackCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        FeedbackItem item = FeedbackItem.Create(
            command.SubmitterUserId,
            command.Type,
            command.Message,
            command.Route,
            command.UserAgent,
            command.Locale,
            command.AppVersion,
            clock);

        await unitOfWork.ExecuteAsync(
            _ =>
            {
                feedback.Add(item);
                return Task.FromResult(true);
            },
            cancellationToken);

        DispatchAdminNotification(item);
        return item.Id;
    }

    private void DispatchAdminNotification(FeedbackItem item)
    {
        if (options.AdminRecipients.Count == 0)
        {
            return;
        }

        Dictionary<string, string> model = new(StringComparer.Ordinal)
        {
            ["Type"] = item.Type.ToString(),
            ["Message"] = item.Message,
            ["Submitter"] = item.SubmitterUserId?.Value.ToString("D", CultureInfo.InvariantCulture)
                ?? "anonymous",
            ["Route"] = item.Route ?? "-",
            ["Locale"] = item.Locale ?? "-",
            ["AppVersion"] = item.AppVersion ?? "-",
            ["UserAgent"] = item.UserAgent ?? "-",
            ["SubmittedAt"] = InstantPattern.ExtendedIso.Format(item.CreatedAt),
        };

        foreach (string recipient in options.AdminRecipients)
        {
            emailDispatcher.TryDispatch(
                new EmailMessage(
                    recipient,
                    AdminLocale,
                    EmailTemplate.AdminFeedbackSubmitted,
                    model));
        }
    }
}
