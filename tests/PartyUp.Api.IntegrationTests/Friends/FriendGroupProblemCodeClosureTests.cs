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

    /// <summary>
    /// The subset of <see cref="FriendGroupStatusForCodes"/> that is NOT shared with any other
    /// feature's <c>StatusFor</c> switch -- <c>validation_failed</c>/<c>user_not_found</c> are
    /// reused by Sessions/Polls too, so only these four are safe to close both ways against
    /// <c>ProblemCodes</c>' own contiguous "FriendGroup HTTP surface" block.
    /// </summary>
    private static readonly string[] FriendGroupOwnCodes =
    [
        "group_not_found",
        "group_limit_reached",
        "group_member_limit_reached",
        "group_name_taken",
    ];

    [Fact]
    public void EveryFriendGroupStatusForCodeIsDocumentedInProblemCodes()
    {
        foreach (string code in FriendGroupStatusForCodes)
        {
            Assert.Contains(code, DocumentedProblemCodes());
        }
    }

    /// <summary>
    /// The reverse direction, closed for the codes this feature alone owns (the shared
    /// "validation_failed"/"user_not_found" codes are excluded -- other features' StatusFor
    /// switches document them too, so their presence in <c>ProblemCodes</c> does not prove
    /// <c>FriendGroupEndpoints.StatusFor</c> handles them, and a reflection-based extraction of a
    /// switch expression's cases is not available). This still catches the case this ticket's
    /// finding cared about: a code added to <c>ProblemCodes</c>' FriendGroup block that
    /// <c>StatusFor</c> does not actually handle.
    /// </summary>
    [Fact]
    public void TheFriendGroupOwnProblemCodesBlockContainsNothingStatusForDoesNotHandle()
    {
        string[] documented = DocumentedProblemCodes();
        int start = Array.IndexOf(documented, "group_not_found");
        Assert.True(start >= 0, "group_not_found must be documented.");
        string[] friendGroupBlock = documented[start..];

        Assert.Equal(
            FriendGroupOwnCodes.OrderBy(code => code, StringComparer.Ordinal),
            friendGroupBlock.OrderBy(code => code, StringComparer.Ordinal));
    }

    private static string[] DocumentedProblemCodes()
    {
        FieldInfo field = typeof(PartyUpOpenApiTransformer).GetField(
            "ProblemCodes", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string[])field.GetValue(null)!;
    }
}
