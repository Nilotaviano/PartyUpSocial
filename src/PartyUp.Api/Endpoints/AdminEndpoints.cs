using PartyUp.Api.Auth;
using PartyUp.Api.Hosting;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Deletion;

namespace PartyUp.Api.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminMaintenanceEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/admin/maintenance/deletion-sweep", RunDeletionSweepAsync)
            .WithName("RunDeletionSweep")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<DeletionMaintenanceResponse>();
        return api;
    }

    private static async Task<IResult> RunDeletionSweepAsync(
        IDeletionJournalShipper shipper,
        PurgeExpiredAuthTokensUseCase purgeTokens,
        ExecuteDueDeletionsUseCase sweep,
        DeletionSweepOptions options,
        CancellationToken cancellationToken)
    {
        await shipper.ShipPendingAsync(cancellationToken);
        int tokensPurged = await purgeTokens.ExecuteAsync(cancellationToken);
        DeletionSweepResult result = await sweep.ExecuteAsync(
            options.BatchSize,
            cancellationToken);
        return Results.Ok(new DeletionMaintenanceResponse(
            result.Executed,
            result.Failed,
            tokensPurged));
    }
}

public sealed record DeletionMaintenanceResponse(int Executed, int Failed, int TokensPurged);
