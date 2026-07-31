using System.Threading.Channels;
using PartyUp.Application.Email;

namespace PartyUp.Infrastructure.Email;

public sealed class ChannelEmailDispatcher : IEmailDispatcher
{
    private const int Capacity = 100;
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_channel.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("The email dispatch channel is closed.");
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<EmailMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}