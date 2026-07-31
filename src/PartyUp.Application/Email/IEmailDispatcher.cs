namespace PartyUp.Application.Email;

public interface IEmailDispatcher
{
    Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken);
}