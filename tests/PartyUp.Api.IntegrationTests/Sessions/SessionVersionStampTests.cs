using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PartyUp.Api.Security;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Sessions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Api.IntegrationTests.Sessions;

/// <summary>
/// F2/A-071: the version token mechanism must be stable across every process sharing the persisted key
/// ring, not just within the process that issued it. <see cref="SessionVersionStamp"/> proves this by
/// using Data Protection's own Protect/Unprotect on the token payload.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionVersionStampTests : IAsyncLifetime, IDisposable
{
    private readonly PartyUpApiFactory _factory = new();

    public Task InitializeAsync() => _factory.StartAsync();

    public Task DisposeAsync() => _factory.StopAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task TwoIndependentlyConstructedInstancesOverTheSameKeyRing_ValidateEachOthersTokens_A071()
    {
        SessionHttpUser organizer = await SessionEndpointTestSupport.SeedAsync(_factory);
        using SessionHttpSession session = await SessionEndpointTestSupport.SignInAsync(_factory, organizer.Id);
        HttpResponseMessage created = await session.SendUnsafeAsync(
            HttpMethod.Post,
            "/api/v1/sessions",
            JsonContent.Create(new
            {
                title = "Key-ring session",
                startTime = SessionEndpointTestSupport.Iso(_factory.Clock.GetCurrentInstant() + NodaTime.Duration.FromDays(1)),
                minAttendance = 1,
                isPrivate = true,
            }));
        using var body = await SessionEndpointTestSupport.SuccessJsonAsync(created);
        Guid sessionGuid = body.RootElement.GetProperty("session").GetProperty("id").GetGuid();
        var sessionId = SessionId.From(sessionGuid);

        string keyRingPath = Path.Combine(
            Path.GetTempPath(), "partyup-version-stamp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyRingPath);
        try
        {
            // Two separate IDataProtectionProvider instances pointed at the same persisted key ring
            // directory simulate two separate processes (e.g. across a restart or a second replica).
            IDataProtectionProvider providerA = DataProtectionProvider.Create(new DirectoryInfo(keyRingPath));
            IDataProtectionProvider providerB = DataProtectionProvider.Create(new DirectoryInfo(keyRingPath));

            await using AsyncServiceScope scopeA = _factory.Services.CreateAsyncScope();
            await using AsyncServiceScope scopeB = _factory.Services.CreateAsyncScope();
            AppDbContext contextA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
            AppDbContext contextB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

            Session sessionA = await contextA.Sessions.SingleAsync(s => s.Id == sessionId);
            Session sessionB = await contextB.Sessions.SingleAsync(s => s.Id == sessionId);

            var stampA = new SessionVersionStamp(contextA, providerA);
            var stampB = new SessionVersionStamp(contextB, providerB);

            uint xmin = contextA.Entry(sessionA).Property<uint>("xmin").CurrentValue;
            string token = stampA.Issue(sessionA.Id, xmin);

            Assert.True(
                stampB.Matches(sessionB, token),
                "A second, independently-constructed stamp instance over the same key-ring directory " +
                "must validate a token issued by the first.");
        }
        finally
        {
            Directory.Delete(keyRingPath, recursive: true);
        }
    }
}
