using System.Reflection;
using PartyUp.Api.Hosting;

namespace PartyUp.Api.IntegrationTests.Friends;

/// <summary>
/// D-198/D-199: every code <c>FriendGroupEndpoints.StatusFor</c> handles must appear in
/// <see cref="PartyUpOpenApiTransformer"/>'s private <c>ProblemCodes</c> registry -- the OpenAPI
/// <c>code</c> enum is the mobile contract, and a code <c>StatusFor</c> maps to a status without
/// being documented there silently lies to callers. No HTTP surface exercised, no DB.
/// </summary>
public sealed class FriendGroupProblemCodeClosureTests
{
    /// <summary>
    /// Mirrors <c>FriendGroupEndpoints.StatusFor</c>'s switch verbatim -- every code the method
    /// maps to a status, including the shared "validation_failed"/"user_not_found" codes.
    /// </summary>
    private static readonly string[] FriendGroupStatusForCodes =
    [
        "validation_failed",
        "group_not_found",
        "user_not_found",
        "group_limit_reached",
        "group_member_limit_reached",
        "group_name_taken",
    ];

    [Fact]
    public void EveryFriendGroupStatusForCodeIsDocumentedInProblemCodes()
    {
        FieldInfo field = typeof(PartyUpOpenApiTransformer).GetField(
            "ProblemCodes", BindingFlags.NonPublic | BindingFlags.Static)!;
        var documentedCodes = (string[])field.GetValue(null)!;

        foreach (string code in FriendGroupStatusForCodes)
        {
            Assert.Contains(code, documentedCodes);
        }
    }
}
