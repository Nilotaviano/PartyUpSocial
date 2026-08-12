using Microsoft.Extensions.Logging;
using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Accounts;
using PartyUp.Application.Auth;
using PartyUp.Application.Notifications;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Email;

/// <summary>
/// D-189 §5: post-commit invite/approval email fan-out for the five call sites named in
/// D189_DESIGN.md §1. Reads only the committed aggregate + the dequeued events its caller already
/// holds — never a post-commit re-read — and never lets a mail failure surface to the caller.
/// </summary>
public sealed partial class InviteEmailService(
    IEmailRecipientReader recipientReader,
    ISecurityTokenService tokens,
    IEmailDispatcher dispatcher,
    IUnitOfWork unitOfWork,
    AccountEmailOptions emailOptions,
    ILogger<InviteEmailService> logger)
{
    private static readonly Duration UnsubscribeTtl = Duration.FromDays(365);

    public Task NotifySessionEventsAsync(
        Session session,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(events);

        List<Target> targets = [];
        foreach (IDomainEvent domainEvent in events)
        {
            switch (domainEvent)
            {
                case SessionInvitationSent invitation:
                    targets.Add(new Target(invitation.InviteeId, EmailTemplate.SessionInvitation, IncludeStartTime: true));
                    break;
                case SessionJoinRequestAccepted accepted:
                    targets.Add(new Target(accepted.RequesterId, EmailTemplate.SessionJoinRequestApproved, IncludeStartTime: false));
                    break;
            }
        }

        return DispatchAsync(
            targets,
            session.OrganizerId,
            session.Id.Value,
            "sessions",
            session.Title.Value,
            session.Notes.Value,
            sessionStartTime: session.StartTime,
            cancellationToken: cancellationToken);
    }

    public Task NotifyPollEventsAsync(
        Poll poll,
        IReadOnlyList<IDomainEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(poll);
        ArgumentNullException.ThrowIfNull(events);

        List<Target> targets = [];
        foreach (IDomainEvent domainEvent in events)
        {
            switch (domainEvent)
            {
                case PollInvitationSent invitation:
                    targets.Add(new Target(invitation.InviteeId, EmailTemplate.PollInvitation, IncludeStartTime: false));
                    break;
                case PollJoinRequestAccepted accepted:
                    targets.Add(new Target(accepted.RequesterId, EmailTemplate.PollJoinRequestApproved, IncludeStartTime: false));
                    break;
            }
        }

        return DispatchAsync(
            targets,
            poll.OrganizerId,
            poll.Id.Value,
            "polls",
            poll.Title.Value,
            poll.Notes.Value,
            sessionStartTime: null,
            cancellationToken: cancellationToken);
    }

    private async Task DispatchAsync(
        IReadOnlyList<Target> targets,
        UserId organizerId,
        Guid subjectId,
        string linkSegment,
        string title,
        string notes,
        Instant? sessionStartTime,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            List<UserId> batch = [organizerId, .. targets.Select(t => t.RecipientId).Distinct()];
            IReadOnlyList<EmailRecipient> recipients = await recipientReader.GetAsync(batch, cancellationToken);
            Dictionary<UserId, EmailRecipient> byId = recipients.ToDictionary(r => r.UserId);

            if (!byId.TryGetValue(organizerId, out EmailRecipient? organizer))
            {
                return;
            }

            List<Target> finalTargets = [.. targets.Where(t =>
                byId.TryGetValue(t.RecipientId, out EmailRecipient? candidate) &&
                candidate.Email is not null &&
                candidate.EmailNotificationsEnabled)];

            if (finalTargets.Count == 0)
            {
                return;
            }

            List<UserId> finalRecipientIds = [.. finalTargets.Select(t => t.RecipientId).Distinct()];

            Dictionary<UserId, string> rawTokensByRecipient = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    Dictionary<UserId, string> issued = [];
                    foreach (UserId recipientId in finalRecipientIds)
                    {
                        IssuedToken token = await tokens.IssueAsync(
                            new IssueTokenRequest(
                                AuthTokenPurpose.EmailUnsubscribe,
                                recipientId,
                                null,
                                UnsubscribeTtl),
                            ct);
                        issued[recipientId] = token.RawToken;
                    }

                    return issued;
                },
                cancellationToken);

            string publicBaseUrl = emailOptions.PublicBaseUrl.TrimEnd('/');
            string link = $"{publicBaseUrl}/{linkSegment}/{subjectId}";

            foreach (Target target in finalTargets)
            {
                EmailRecipient recipient = byId[target.RecipientId];
                string locale = EmbeddedEmailTemplateRenderer.CanonicalizeLocale(recipient.Locale?.Value);

                Dictionary<string, string> model = new(StringComparer.Ordinal)
                {
                    ["RecipientName"] = recipient.DisplayName,
                    ["OrganizerName"] = organizer.DisplayName,
                    ["Title"] = title,
                    ["Notes"] = notes,
                    ["Link"] = link,
                    ["PublicBaseUrl"] = publicBaseUrl,
                    ["Token"] = rawTokensByRecipient[target.RecipientId],
                };

                if (target.IncludeStartTime && sessionStartTime is { } startTime)
                {
                    DateTimeZone zone = recipient.DateTimeZone ?? DateTimeZone.Utc;
                    model["StartTime"] = LocalizedInstantFormatter.FormatInstant(startTime, zone, locale);
                }

                dispatcher.TryDispatch(new EmailMessage(
                    recipient.Email!,
                    Domain.Users.Locale.Create(locale),
                    target.Template,
                    model));
            }
        }
        catch (Exception exception)
        {
            LogDispatchFailed(logger, linkSegment, subjectId, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "D-189 invite email dispatch failed for {LinkSegment}/{SubjectId}.")]
    private static partial void LogDispatchFailed(
        ILogger logger,
        string linkSegment,
        Guid subjectId,
        Exception exception);

    private sealed record Target(UserId RecipientId, EmailTemplate Template, bool IncludeStartTime);
}
