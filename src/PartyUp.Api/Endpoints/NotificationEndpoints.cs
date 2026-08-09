using PartyUp.Api.Auth;
using PartyUp.Api.Contracts.Notifications;
using PartyUp.Api.Contracts.Sessions;
using PartyUp.Api.ErrorHandling;
using PartyUp.Application.Abstractions;
using PartyUp.Application.Notifications;
using PartyUp.Application.Push;
using PartyUp.Application.Sessions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Notifications;

namespace PartyUp.Api.Endpoints;

/// <summary>
/// PHASE7_DESIGN.md §4's push-subscription + notification-center HTTP surface, registered-user only
/// (D-167: never on the guest <c>MapGroup</c>). Session/poll mute routes live on their own owning
/// endpoint files (<c>SessionEndpoints</c>/<c>PollEndpoints</c>).
/// </summary>
public static class NotificationEndpoints
{
    private const int PageSize = 20;

    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        RouteGroupBuilder group = api.MapGroup(string.Empty)
            .RequireAuthorization(AuthorizationPolicies.RequireOnboarded);

        Common(group.MapGet("/push/config", GetPushConfigAsync)
                .WithName(ApiOperationIds.GetPushConfig)
                .Produces<PushConfigResponse>());

        BodyOperation(group.MapPost("/push/subscriptions", CreatePushSubscriptionAsync)
                .WithName(ApiOperationIds.CreatePushSubscription)
                .Produces<CreatePushSubscriptionResponse>(StatusCodes.Status201Created))
            .ProducesPartyUpProblem(StatusCodes.Status409Conflict);

        Unsafe(group.MapDelete("/push/subscriptions/{subscriptionId:guid}", DeletePushSubscriptionAsync)
                .WithName(ApiOperationIds.DeletePushSubscription)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Common(group.MapGet("/notifications", ListNotificationsAsync)
                .WithName(ApiOperationIds.ListNotifications)
                .Produces<SessionPageResponse<NotificationResponse>>())
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest);

        Common(group.MapGet("/notifications/unread-count", GetUnreadNotificationCountAsync)
                .WithName(ApiOperationIds.GetUnreadNotificationCount)
                .Produces<UnreadNotificationCountResponse>());

        Unsafe(group.MapPost("/notifications/{notificationId:guid}/read", MarkNotificationReadAsync)
                .WithName(ApiOperationIds.MarkNotificationRead)
                .Produces(StatusCodes.Status204NoContent))
            .ProducesPartyUpProblem(StatusCodes.Status404NotFound);

        Unsafe(group.MapPost("/notifications/read-all", MarkAllNotificationsReadAsync)
                .WithName(ApiOperationIds.MarkAllNotificationsRead)
                .Produces(StatusCodes.Status204NoContent));

        return api;
    }

    // ---- push -------------------------------------------------------------------------------

    private static async Task<IResult> GetPushConfigAsync(
        ICurrentUserAccessor currentUser,
        IPushSender sender,
        IConfiguration configuration,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        string? vapidPublicKey = sender.IsConfigured ? configuration["Push:Vapid:PublicKey"] : null;
        return Results.Ok(new PushConfigResponse(vapidPublicKey, sender.IsConfigured));
    }

    private static async Task<IResult> CreatePushSubscriptionAsync(
        CreatePushSubscriptionRequest request,
        ICurrentUserAccessor currentUser,
        CreatePushSubscriptionUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        CreatePushSubscriptionResult result;
        try
        {
            result = await useCase.ExecuteAsync(
                actorId.Value,
                request.Channel ?? string.Empty,
                request.Endpoint ?? string.Empty,
                request.PublicKey ?? string.Empty,
                request.AuthSecret ?? string.Empty,
                cancellationToken);
        }
        catch (DomainException)
        {
            // PushSubscription.Create's own field-length/emptiness guards -- distinct from the
            // channel/§8-configuration outcomes the use case itself returns as a typed failure.
            return Problem(context, StatusCodes.Status400BadRequest, "validation_failed");
        }

        if (!result.Succeeded)
        {
            int status = result.Code == "push_not_configured"
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Problem(context, status, result.Code!);
        }

        return Results.Created(
            $"{ApiRoutes.V1}/push/subscriptions/{result.SubscriptionId}",
            new CreatePushSubscriptionResponse(result.SubscriptionId!.Value));
    }

    private static async Task<IResult> DeletePushSubscriptionAsync(
        Guid subscriptionId,
        ICurrentUserAccessor currentUser,
        DeletePushSubscriptionUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        bool found = await useCase.ExecuteAsync(
            PushSubscriptionId.From(subscriptionId), actorId.Value, cancellationToken);
        return found
            ? Results.NoContent()
            : Problem(context, StatusCodes.Status404NotFound, "push_subscription_not_found");
    }

    // ---- notification center ------------------------------------------------------------------

    private static async Task<IResult> ListNotificationsAsync(
        string? scope,
        string? cursor,
        ICurrentUserAccessor currentUser,
        ListNotificationsUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (scope is not null && scope is not ("all" or "unread"))
        {
            return Problem(context, StatusCodes.Status400BadRequest, "invalid_scope");
        }

        bool unreadOnly = scope == "unread";
        SessionListScope listScope = unreadOnly
            ? SessionListScope.NotificationsUnread
            : SessionListScope.NotificationsAll;

        SessionListCursor? decodedCursor = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            decodedCursor = SessionListCursor.TryDecode(cursor, listScope);
            if (decodedCursor is null)
            {
                return Problem(context, StatusCodes.Status400BadRequest, "invalid_cursor");
            }
        }

        SessionPage<NotificationReadModel> page = await useCase.ExecuteAsync(
            actorId.Value, unreadOnly, decodedCursor, PageSize, cancellationToken);
        return Results.Ok(SessionPageResponse.From(page, NotificationResponse.From));
    }

    private static async Task<IResult> GetUnreadNotificationCountAsync(
        ICurrentUserAccessor currentUser,
        ListNotificationsUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        int count = await useCase.UnreadCountAsync(actorId.Value, cancellationToken);
        return Results.Ok(new UnreadNotificationCountResponse(count));
    }

    private static async Task<IResult> MarkNotificationReadAsync(
        Guid notificationId,
        ICurrentUserAccessor currentUser,
        MarkNotificationReadUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        bool found = await useCase.ExecuteAsync(
            NotificationId.From(notificationId), actorId.Value, cancellationToken);
        return found
            ? Results.NoContent()
            : Problem(context, StatusCodes.Status404NotFound, "notification_not_found");
    }

    private static async Task<IResult> MarkAllNotificationsReadAsync(
        ICurrentUserAccessor currentUser,
        MarkAllNotificationsReadUseCase useCase,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        UserId? actorId = await ActorIdAsync(currentUser, cancellationToken);
        if (actorId is null)
        {
            return Problem(context, StatusCodes.Status401Unauthorized, "unauthorized");
        }

        await useCase.ExecuteAsync(actorId.Value, cancellationToken);
        return Results.NoContent();
    }

    // ---- shared helpers ------------------------------------------------------------------------

    private static async Task<UserId?> ActorIdAsync(
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken) =>
        (await currentUser.GetAsync(cancellationToken))?.Id;

    private static IResult Problem(HttpContext context, int status, string code) =>
        PartyUpProblemResults.Create(context, status, code);

    private static RouteHandlerBuilder Common(RouteHandlerBuilder builder) =>
        builder
            .ProducesPartyUpProblem(StatusCodes.Status401Unauthorized)
            .ProducesPartyUpProblem(StatusCodes.Status403Forbidden)
            .ProducesPartyUpProblem(StatusCodes.Status429TooManyRequests)
            .ProducesPartyUpProblem(StatusCodes.Status500InternalServerError);

    private static RouteHandlerBuilder Unsafe(RouteHandlerBuilder builder) =>
        Common(builder)
            .ProducesPartyUpProblem(StatusCodes.Status400BadRequest)
            .ProducesPartyUpProblem(StatusCodes.Status503ServiceUnavailable);

    private static RouteHandlerBuilder BodyOperation(RouteHandlerBuilder builder) =>
        Unsafe(builder)
            .ProducesPartyUpProblem(StatusCodes.Status415UnsupportedMediaType);
}
