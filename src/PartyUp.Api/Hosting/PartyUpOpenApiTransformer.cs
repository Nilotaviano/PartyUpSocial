using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PartyUp.Api.Hosting;

public sealed class PartyUpOpenApiTransformer :
    IOpenApiDocumentTransformer,
    IOpenApiOperationTransformer
{
    private static readonly string[] ProblemCodes =
    [
        "validation_failed",
        "friend_code_invalid",
        "friend_request_self",
        "block_self",
        "antiforgery_failed",
        "unauthorized",
        "onboarding_required",
        "user_not_found",
        "friend_request_not_found",
        "friendship_not_found",
        "block_not_found",
        "friend_request_already_exists",
        "friendship_already_exists",
        "friend_request_not_pending",
        "steam_not_linked",
        "unsupported_media_type",
        "rate_limited",
        "internal_error",
    ];

    private static readonly HashSet<string> FriendOperationIds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ApiOperationIds.GetFriendShare,
            ApiOperationIds.GetUserProfile,
            ApiOperationIds.ResolveFriendCode,
            ApiOperationIds.ListFriends,
            ApiOperationIds.ListFriendRequests,
            ApiOperationIds.SendFriendRequest,
            ApiOperationIds.AcceptFriendRequest,
            ApiOperationIds.RejectFriendRequest,
            ApiOperationIds.CancelFriendRequest,
            ApiOperationIds.Unfriend,
            ApiOperationIds.ListBlocks,
            ApiOperationIds.BlockUser,
            ApiOperationIds.UnblockUser,
            ApiOperationIds.ImportSteamFriends,
        };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes["cookieAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "pu.auth",
            Description = "PartyUp browser session cookie.",
        };

        if (document.Components.Schemas is { } schemas)
        {
            if (schemas.TryGetValue("ProfileViewResponse", out IOpenApiSchema? profileSchema) &&
                profileSchema is OpenApiSchema profile)
            {
                profile.OneOf = profile.AnyOf;
                profile.AnyOf = null;
            }

            if (schemas.TryGetValue(
                    "SteamFriendImportResponse",
                    out IOpenApiSchema? steamImportSchema) &&
                steamImportSchema is OpenApiSchema steamImport)
            {
                steamImport.OneOf = steamImport.AnyOf;
                steamImport.AnyOf = null;
            }

            if (schemas.TryGetValue(
                    "FriendRelationshipResponse",
                    out IOpenApiSchema? relationshipSchema) &&
                relationshipSchema is OpenApiSchema relationship)
            {
                relationship.Required?.Remove("friendshipId");
                SetStringEnum(relationship, "kind",
                    "none",
                    "incoming_pending",
                    "outgoing_pending",
                    "accepted");
            }

            if (schemas.TryGetValue(
                    "FriendshipResourceResponse",
                    out IOpenApiSchema? friendshipResourceSchema) &&
                friendshipResourceSchema is OpenApiSchema friendshipResource)
            {
                friendshipResource.Required?.Remove("acceptedAt");
                SetStringEnum(friendshipResource, "status", "pending", "accepted");
                SetStringEnum(
                    friendshipResource,
                    "direction",
                    "outgoing",
                    "incoming",
                    "accepted");
            }

            if (schemas.TryGetValue(
                    "SteamUnmatchedFriendResponse",
                    out IOpenApiSchema? unmatchedFriendSchema) &&
                unmatchedFriendSchema is OpenApiSchema unmatchedFriend)
            {
                unmatchedFriend.Required?.Remove("personaName");
                unmatchedFriend.Required?.Remove("avatarUrl");
            }

            if (schemas.TryGetValue(
                    "PartyUpProblem",
                    out IOpenApiSchema? problemSchema) &&
                problemSchema is OpenApiSchema problem)
            {
                SetStringEnum(problem, "code", ProblemCodes);
                schemas["UnauthorizedPartyUpProblem"] = ExactProblemSchema(
                    document,
                    "unauthorized");
                schemas["OnboardingRequiredPartyUpProblem"] = ExactProblemSchema(
                    document,
                    "onboarding_required");
            }

            if (schemas.TryGetValue("SessionResponse", out IOpenApiSchema? sessionSchema) &&
                sessionSchema is OpenApiSchema session)
            {
                session.Required?.Remove("continuation");
            }

            SetObjectType(schemas, "ProfileViewResponseUsernameOnlyProfileResponse");
            SetObjectType(schemas, "ProfileViewResponseFullProfileResponse");
            SetObjectType(schemas, "SteamFriendImportResponseReadySteamFriendImportResponse");
            SetObjectType(
                schemas,
                "SteamFriendImportResponsePrivateProfileSteamFriendImportResponse");
            SetObjectType(
                schemas,
                "SteamFriendImportResponseZeroFriendsSteamFriendImportResponse");
            SetObjectType(
                schemas,
                "SteamFriendImportResponseZeroMatchesSteamFriendImportResponse");
            SetObjectType(
                schemas,
                "SteamFriendImportResponseNotConfiguredSteamFriendImportResponse");
            SetObjectType(
                schemas,
                "SteamFriendImportResponseUpstreamFailureSteamFriendImportResponse");
        }

        var cookieReference = new OpenApiSecuritySchemeReference(
            "cookieAuth",
            document,
            externalResource: null);
        foreach (IOpenApiPathItem path in document.Paths.Values)
        {
            if (path.Operations is not { } operations)
            {
                continue;
            }

            foreach (OpenApiOperation operation in operations.Values)
            {
                if (operation.OperationId is null ||
                    !FriendOperationIds.Contains(operation.OperationId))
                {
                    continue;
                }

                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [cookieReference] = [],
                });
                SetProblemResponseSchema(
                    operation,
                    document,
                    "401",
                    "UnauthorizedPartyUpProblem");
                SetProblemResponseSchema(
                    operation,
                    document,
                    "403",
                    "OnboardingRequiredPartyUpProblem");
            }
        }
        return Task.CompletedTask;
    }

    private static OpenApiSchema ExactProblemSchema(
        OpenApiDocument document,
        string code) =>
        new()
        {
            AllOf =
            [
                new OpenApiSchemaReference(
                    "PartyUpProblem",
                    document,
                    externalResource: null),
            ],
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                ["code"] = StringEnum(code),
            },
            Required = new HashSet<string>(StringComparer.Ordinal) { "code" },
        };

    private static void SetProblemResponseSchema(
        OpenApiOperation operation,
        OpenApiDocument document,
        string status,
        string schemaName)
    {
        if (operation.Responses is null ||
            !operation.Responses.TryGetValue(status, out IOpenApiResponse? response) ||
            response is not OpenApiResponse concreteResponse ||
            concreteResponse.Content is null ||
            !concreteResponse.Content.TryGetValue(
                "application/problem+json",
                out OpenApiMediaType? mediaType))
        {
            return;
        }

        mediaType.Schema = new OpenApiSchemaReference(
            schemaName,
            document,
            externalResource: null);
    }

    private static void SetStringEnum(
        OpenApiSchema objectSchema,
        string propertyName,
        params string[] values)
    {
        if (objectSchema.Properties is not null &&
            objectSchema.Properties.TryGetValue(
                propertyName,
                out IOpenApiSchema? property) &&
            property is OpenApiSchema concreteProperty)
        {
            concreteProperty.Enum = values
                .Select(value => (JsonNode)JsonValue.Create(value)!)
                .ToList();
        }
    }

    private static OpenApiSchema StringEnum(params string[] values) =>
        new()
        {
            Type = JsonSchemaType.String,
            Enum = values
                .Select(value => (JsonNode)JsonValue.Create(value)!)
                .ToList(),
        };

    private static void SetObjectType(
        IDictionary<string, IOpenApiSchema> schemas,
        string schemaName)
    {
        if (schemas.TryGetValue(schemaName, out IOpenApiSchema? schema) &&
            schema is OpenApiSchema objectSchema)
        {
            objectSchema.Type = JsonSchemaType.Object;
        }
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        string method = context.Description.HttpMethod ?? string.Empty;
        if (HttpMethods.IsGet(method) ||
            HttpMethods.IsHead(method) ||
            HttpMethods.IsOptions(method) ||
            HttpMethods.IsTrace(method))
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "X-CSRF-TOKEN",
            In = ParameterLocation.Header,
            Required = true,
            Description = "Request token returned by GET /api/v1/auth/antiforgery.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        return Task.CompletedTask;
    }
}
