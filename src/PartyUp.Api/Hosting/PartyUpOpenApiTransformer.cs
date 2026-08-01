using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PartyUp.Api.Hosting;

public sealed class PartyUpOpenApiTransformer :
    IOpenApiDocumentTransformer,
    IOpenApiOperationTransformer
{
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
                    "FriendRelationshipResponse",
                    out IOpenApiSchema? relationshipSchema) &&
                relationshipSchema is OpenApiSchema relationship)
            {
                relationship.Required?.Remove("friendshipId");
            }

            if (schemas.TryGetValue("SessionResponse", out IOpenApiSchema? sessionSchema) &&
                sessionSchema is OpenApiSchema session)
            {
                session.Required?.Remove("continuation");
            }

            SetObjectType(schemas, "ProfileViewResponseUsernameOnlyProfileResponse");
            SetObjectType(schemas, "ProfileViewResponseFullProfileResponse");
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
            }
        }
        return Task.CompletedTask;
    }

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
