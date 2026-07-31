using Microsoft.Extensions.Logging.Abstractions;
using PartyUp.Api.Hosting;
using PartyUp.Application.Email;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Email;

namespace PartyUp.Api.IntegrationTests;

public sealed class EmailDispatcherTests
{
    [Fact]
    public async Task Dispatch_WithAnAlreadyCanceledRequestToken_ReachesThePump()
    {
        var dispatcher = new ChannelEmailDispatcher();
        var sender = new CapturingEmailSender();
        var service = new EmailDispatchHostedService(
            dispatcher,
            sender,
            NullLogger<EmailDispatchHostedService>.Instance);
        EmailMessage filler = Message("filler@example.test");
        for (int index = 0; index < 100; index++)
        {
            await dispatcher.DispatchAsync(filler, default);
        }

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        EmailMessage expected = Message("Player@corp.example");
        Task enqueue = dispatcher.DispatchAsync(expected, canceled.Token);

        await service.StartAsync(default);
        try
        {
            await enqueue;
            EmailMessage delivered = await sender.WaitForAsync(
                message => message.ToAddress == expected.ToAddress);
            Assert.Equal(expected, delivered);
        }
        finally
        {
            await service.StopAsync(default);
        }
    }

    private static EmailMessage Message(string address) =>
        new(
            address,
            Locale.Create("en"),
            EmailTemplate.EmailVerification,
            new Dictionary<string, string>());
}
