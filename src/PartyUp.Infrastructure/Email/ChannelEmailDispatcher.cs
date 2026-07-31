using System.Threading.Channels;
using PartyUp.Application.Email;

namespace PartyUp.Infrastructure.Email;

public sealed class ChannelEmailDispatcher : IEmailDispatcher
{
    private const int Capacity = 100;
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public async Task DispatchAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<EmailMessage> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
