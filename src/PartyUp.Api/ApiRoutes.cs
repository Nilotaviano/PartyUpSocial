namespace PartyUp.Api;

public static class ApiRoutes
{
    public const string V1 = "/api/v1";
}

public static class ApiOperationIds
{
    public const string GetAntiforgery = "GetAntiforgery";
    public const string GetSession = "GetSession";
    public const string Logout = "Logout";
    public const string Login = "Login";
    public const string Register = "Register";
    public const string RequestMagicLink = "RequestMagicLink";
    public const string ConsumeMagicLink = "ConsumeMagicLink";
    public const string ForgotPassword = "ForgotPassword";
    public const string ResetPassword = "ResetPassword";
    public const string ChangePassword = "ChangePassword";
    public const string SetPassword = "SetPassword";
    public const string ChangeEmail = "ChangeEmail";
    public const string RequestEmailVerification = "RequestEmailVerification";
    public const string VerifyEmail = "VerifyEmail";
    public const string IssueExternalIntent = "IssueExternalIntent";
    public const string Reauthenticate = "Reauthenticate";
    public const string ReauthenticateByEmail = "ReauthenticateByEmail";
    public const string DeleteAuthMethod = "DeleteAuthMethod";
    public const string DeletePlatformLink = "DeletePlatformLink";
    public const string RequestAccountDeletion = "RequestAccountDeletion";
    public const string StartSteamDeletion = "StartSteamDeletion";
    public const string CompleteSteamDeletion = "CompleteSteamDeletion";
    public const string DeleteMe = "DeleteMe";
    public const string CheckUsernameAvailability = "CheckUsernameAvailability";
    public const string GetFriendShare = "GetFriendShare";
    public const string GetUserProfile = "GetUserProfile";
    public const string ResolveFriendCode = "ResolveFriendCode";
    public const string ListFriends = "ListFriends";
    public const string ListFriendRequests = "ListFriendRequests";
    public const string SendFriendRequest = "SendFriendRequest";
    public const string AcceptFriendRequest = "AcceptFriendRequest";
    public const string RejectFriendRequest = "RejectFriendRequest";
    public const string CancelFriendRequest = "CancelFriendRequest";
    public const string Unfriend = "Unfriend";
    public const string ListBlocks = "ListBlocks";
    public const string BlockUser = "BlockUser";
    public const string UnblockUser = "UnblockUser";
    public const string ImportSteamFriends = "ImportSteamFriends";
}
