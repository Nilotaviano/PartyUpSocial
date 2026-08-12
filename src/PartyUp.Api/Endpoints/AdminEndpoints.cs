using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Text;
using PartyUp.Api.Auth;
using PartyUp.Api.Hosting;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Admin;
using PartyUp.Application.Deletion;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;
using PartyUp.Domain.Users.DeletionExecution;

namespace PartyUp.Api.Endpoints;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminMaintenanceEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/users", ListUsersAsync)
            .WithName("ListAdminUsers")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<AdminUsersResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/admin/users/{id:guid}", GetUserAsync)
            .WithName("GetAdminUser")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<AdminUserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapPost("/admin/users/{id:guid}/ban", BanUserAsync)
            .WithName("BanAdminUser")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<AdminMutationResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented);
        api.MapPost("/admin/users/{id:guid}/unban", UnbanUserAsync)
            .WithName("UnbanAdminUser")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<AdminMutationResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/admin/dashboard", GetDashboardAsync)
            .WithName("GetAdminDashboard")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<AdminDashboardResponse>();
        api.MapPost("/admin/maintenance/deletion-sweep", RunDeletionSweepAsync)
            .WithName("RunDeletionSweep")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<DeletionMaintenanceResponse>();
        api.MapGet("/admin/deletion-journal", ReadDeletionJournalAsync)
            .WithName("ReadDeletionJournal")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin)
            .Produces<DeletionJournalResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return api;
    }

    private static async Task<IResult> ListUsersAsync(
        HttpContext context,
        ListUsersUseCase useCase,
        CancellationToken cancellationToken,
        string? query = null,
        int page = 1,
        int pageSize = 50)
    {
        if (page < 1 || pageSize is < 1 or > 100)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        AdminUsersResult result = await useCase.ExecuteAsync(
            query,
            page,
            pageSize,
            cancellationToken);
        return Results.Ok(new AdminUsersResponse(
            result.Users.Select(AdminUserResponse.From).ToArray(),
            result.Page,
            result.PageSize,
            result.Total));
    }

    private static async Task<IResult> GetUserAsync(
        Guid id,
        HttpContext context,
        GetUserUseCase useCase,
        CancellationToken cancellationToken)
    {
        AdminUser? user = await useCase.ExecuteAsync(UserId.From(id), cancellationToken);
        return user is null
            ? Problem(context, StatusCodes.Status404NotFound, "not_found")
            : Results.Ok(AdminUserResponse.From(user));
    }

    private static async Task<IResult> GetDashboardAsync(
        GetUsageDashboardUseCase useCase,
        CancellationToken cancellationToken)
    {
        UsageDashboard dashboard = await useCase.ExecuteAsync(cancellationToken);
        return Results.Ok(AdminDashboardResponse.From(dashboard));
    }

    private static async Task<IResult> BanUserAsync(
        Guid id,
        BanUserRequest request,
        HttpContext context,
        BanUserUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (request.RemoveContent)
        {
            return Problem(
                context,
                StatusCodes.Status501NotImplemented,
                "not_implemented_in_phase");
        }

        AdminUserMutationResult result = await useCase.ExecuteAsync(
            UserId.From(id),
            cancellationToken);
        return result.Succeeded
            ? Results.Ok(new AdminMutationResponse("banned"))
            : Problem(context, StatusCodes.Status404NotFound, result.Code!);
    }

    private static async Task<IResult> UnbanUserAsync(
        Guid id,
        HttpContext context,
        UnbanUserUseCase useCase,
        CancellationToken cancellationToken)
    {
        IResult? malformedBody = await AuthEndpoints.ValidateOptionalJsonBodyAsync(
            context,
            cancellationToken);
        if (malformedBody is not null)
        {
            return malformedBody;
        }

        AdminUserMutationResult result = await useCase.ExecuteAsync(
            UserId.From(id),
            cancellationToken);
        return result.Succeeded
            ? Results.Ok(new AdminMutationResponse("unbanned"))
            : Problem(context, StatusCodes.Status404NotFound, result.Code!);
    }

    private static async Task<IResult> ReadDeletionJournalAsync(
        string? since,
        HttpContext context,
        ReadDeletionJournalUseCase useCase,
        CancellationToken cancellationToken,
        int limit = 100)
    {
        ParseResult<Instant> parsed = InstantPattern.ExtendedIso.Parse(since ?? string.Empty);
        if (!parsed.Success || limit is < 1 or > 1000)
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_request");
        }

        IReadOnlyList<DeletionJournalEntry> entries = await useCase.ExecuteAsync(
            parsed.Value,
            limit,
            cancellationToken);
        return Results.Ok(new DeletionJournalResponse(
            entries.Select(entry => new DeletionJournalItemResponse(
                entry.UserId.Value,
                InstantPattern.ExtendedIso.Format(entry.ExecutedAt))).ToArray()));
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

    private static IResult Problem(HttpContext context, int status, string code)
    {
        string title = status switch
        {
            StatusCodes.Status400BadRequest => "Bad Request",
            StatusCodes.Status404NotFound => "Not Found",
            StatusCodes.Status501NotImplemented => "Not Implemented",
            _ => "Request Failed",
        };
        return Results.Json(
            new AuthProblem(
                "about:blank",
                title,
                status,
                code,
                context.TraceIdentifier),
            statusCode: status,
            contentType: "application/problem+json");
    }
}

public sealed record DeletionMaintenanceResponse(int Executed, int Failed, int TokensPurged);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BanUserRequest(bool RemoveContent);

public sealed record AdminMutationResponse(string Status);

public sealed record AdminUsersResponse(
    IReadOnlyList<AdminUserResponse> Users,
    int Page,
    int PageSize,
    int Total);

public sealed record AdminUserResponse(
    Guid Id,
    string? Username,
    string? DisplayName,
    string? Email,
    string Created,
    string Lifecycle,
    string? BannedAt)
{
    internal static AdminUserResponse From(AdminUser user) =>
        new(
            user.Id.Value,
            user.Username,
            user.DisplayName,
            user.Email,
            InstantPattern.ExtendedIso.Format(user.CreatedAt),
            LifecycleName(user.Lifecycle),
            user.BannedAt is { } bannedAt
                ? InstantPattern.ExtendedIso.Format(bannedAt)
                : null);

    private static string LifecycleName(UserLifecycle lifecycle) => lifecycle switch
    {
        UserLifecycle.Onboarding => "onboarding",
        UserLifecycle.Active => "active",
        UserLifecycle.PendingDeletion => "pending_deletion",
        UserLifecycle.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
    };
}

public sealed record AdminDashboardResponse(
    string GeneratedAt,
    AdminUsageWindowResponse Last7Days,
    AdminUsageWindowResponse Last30Days,
    AdminRepeatSchedulingResponse RepeatScheduling)
{
    internal static AdminDashboardResponse From(UsageDashboard dashboard) =>
        new(
            InstantPattern.ExtendedIso.Format(dashboard.GeneratedAt),
            AdminUsageWindowResponse.From(dashboard.Last7Days),
            AdminUsageWindowResponse.From(dashboard.Last30Days),
            new AdminRepeatSchedulingResponse(
                dashboard.RepeatScheduling.Days,
                dashboard.RepeatScheduling.Organizers,
                dashboard.RepeatScheduling.RepeatOrganizers,
                dashboard.RepeatScheduling.Rate));
}

public sealed record AdminUsageWindowResponse(
    int Days,
    string Since,
    int Registrations,
    int ActiveUsers,
    int SessionsCreated,
    int PollsCreated,
    int ConfirmationDenominator,
    int ConfirmationSuccesses,
    double? ConfirmationRate)
{
    internal static AdminUsageWindowResponse From(UsageWindow window) =>
        new(
            window.Days,
            InstantPattern.ExtendedIso.Format(window.Since),
            window.Registrations,
            window.ActiveUsers,
            window.SessionsCreated,
            window.PollsCreated,
            window.ConfirmationDenominator,
            window.ConfirmationSuccesses,
            window.ConfirmationRate);
}

public sealed record AdminRepeatSchedulingResponse(
    int Days,
    int Organizers,
    int RepeatOrganizers,
    double? Rate);

public sealed record DeletionJournalResponse(
    IReadOnlyList<DeletionJournalItemResponse> Entries);

public sealed record DeletionJournalItemResponse(Guid UserId, string ExecutedAt);
