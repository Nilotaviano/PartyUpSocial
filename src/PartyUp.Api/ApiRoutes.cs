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

    // PHASE4_DESIGN.md §8: session HTTP surface, relative to the /api/v1 group.
    public const string CreateSession = "CreateSession";
    public const string ListSessions = "ListSessions";
    public const string GetSessionDetail = "GetSessionDetail";
    public const string EditSession = "EditSession";
    public const string CancelSession = "CancelSession";
    public const string InviteSessionUsers = "InviteSessionUsers";
    public const string CreateSessionGuestLinks = "CreateSessionGuestLinks";
    public const string RevokeSessionGuestLink = "RevokeSessionGuestLink";
    public const string RemoveSessionGuest = "RemoveSessionGuest";
    public const string RespondToSessionInvitation = "RespondToSessionInvitation";
    public const string LeaveSession = "LeaveSession";
    public const string RequestSessionJoin = "RequestSessionJoin";
    public const string WithdrawSessionJoinRequest = "WithdrawSessionJoinRequest";
    public const string ListSessionJoinRequests = "ListSessionJoinRequests";
    public const string ApproveSessionJoinRequest = "ApproveSessionJoinRequest";
    public const string RejectSessionJoinRequest = "RejectSessionJoinRequest";
    public const string PromoteSessionWaitlistParticipant = "PromoteSessionWaitlistParticipant";
    public const string RejectSessionWaitlistParticipant = "RejectSessionWaitlistParticipant";
    public const string SetSessionReminder = "SetSessionReminder";

    // PHASE4_DESIGN.md §8.1: the guest bearer surface. ExchangeGuestLink is the A-064 transport
    // exception outside /api/v1 and outside the OpenAPI document; the rest are ordinary /api/v1
    // guest resource operations.
    public const string ExchangeGuestLink = "ExchangeGuestLink";
    public const string GetGuestSession = "GetGuestSession";
    public const string ClaimGuestSessionLink = "ClaimGuestSessionLink";
    public const string RenameGuestSessionIdentity = "RenameGuestSessionIdentity";
    public const string RespondAsGuest = "RespondAsGuest";

    // PHASE5_DESIGN.md §8: availability, calendar, Home and Discover HTTP surface, relative to /api/v1.
    public const string GetMyAvailability = "GetMyAvailability";
    public const string CreateAvailabilityBlock = "CreateAvailabilityBlock";
    public const string EditAvailabilityBlock = "EditAvailabilityBlock";
    public const string DeleteAvailabilityBlock = "DeleteAvailabilityBlock";
    public const string CreateAvailabilitySeries = "CreateAvailabilitySeries";
    public const string DeleteAvailabilitySeries = "DeleteAvailabilitySeries";
    public const string EditAvailabilityOccurrence = "EditAvailabilityOccurrence";
    public const string DeleteAvailabilityOccurrence = "DeleteAvailabilityOccurrence";
    public const string FindOverlapWindows = "FindOverlapWindows";
    public const string GetHomeSuggestions = "GetHomeSuggestions";
    public const string GetCalendarView = "GetCalendarView";
    public const string GetActionableInbox = "GetActionableInbox";
}
