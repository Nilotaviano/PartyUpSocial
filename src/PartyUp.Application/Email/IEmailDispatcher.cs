namespace PartyUp.Application.Email;

public interface IEmailDispatcher
{
    Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// D-189: non-blocking best-effort enqueue for post-commit fan-out. Returns false and drops
    /// the message when the channel is full — never parks the caller behind a slow drain.
    /// </summary>
    bool TryDispatch(EmailMessage message);
}