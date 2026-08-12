using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using NodaTime;

namespace PartyUp.Application.Email;

/// <summary>
/// D-189 §4: the only per-recipient data source for invite/approval email — a single batched
/// query, never a per-recipient round trip. Every requested user that exists is returned so the
/// organizer's display name is available even without a mailable address; <see cref="EmailRecipient.Email"/>
/// is null unless the account holds a confirmed address, and only non-null emails may be mailed.
/// </summary>
public interface IEmailRecipientReader
{
    Task<IReadOnlyList<EmailRecipient>> GetAsync(
        IReadOnlyList<UserId> userIds,
        CancellationToken cancellationToken);
}

public sealed record EmailRecipient(
    UserId UserId,
    string? Email,
    string DisplayName,
    Locale? Locale,
    DateTimeZone? DateTimeZone,
    bool EmailNotificationsEnabled);
