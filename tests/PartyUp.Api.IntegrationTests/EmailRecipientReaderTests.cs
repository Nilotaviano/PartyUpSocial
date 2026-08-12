using Microsoft.Extensions.DependencyInjection;
using PartyUp.Application.Email;
using PartyUp.Domain.Kernel;

namespace PartyUp.Api.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EmailRecipientReaderTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task GetAsync_ReturnsEveryExistingUserWithEmailOnlyWhenConfirmed_D189()
    {
        UserId confirmed = await _factory.SeedUserAsync(
            email: "confirmed@example.test",
            emailConfirmed: true);
        UserId unconfirmed = await _factory.SeedUserAsync(
            email: "unconfirmed@example.test",
            emailConfirmed: false);
        UserId noEmail = await _factory.SeedUserAsync(email: null);
        UserId nonexistent = UserId.New();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        var reader = scope.ServiceProvider.GetRequiredService<IEmailRecipientReader>();

        IReadOnlyList<EmailRecipient> recipients = await reader.GetAsync(
            [confirmed, unconfirmed, noEmail, nonexistent],
            default);

        Assert.Equal(3, recipients.Count);
        Dictionary<UserId, EmailRecipient> byId = recipients.ToDictionary(r => r.UserId);
        Assert.Equal("confirmed@example.test", byId[confirmed].Email);
        Assert.True(byId[confirmed].EmailNotificationsEnabled);
        Assert.Null(byId[unconfirmed].Email);
        Assert.Null(byId[noEmail].Email);
        Assert.DoesNotContain(nonexistent, byId.Keys);
    }
}
