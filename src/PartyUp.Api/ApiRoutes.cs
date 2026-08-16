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

    // D-198/D-199: the friend group HTTP surface, relative to /api/v1.
    public const string ListFriendGroups = "ListFriendGroups";
    public const string CreateFriendGroup = "CreateFriendGroup";
    public const string GetFriendGroup = "GetFriendGroup";
    public const string RenameFriendGroup = "RenameFriendGroup";
    public const string DeleteFriendGroup = "DeleteFriendGroup";
    public const string InviteFriendGroupMembers = "InviteFriendGroupMembers";
    public const string RemoveFriendGroupMember = "RemoveFriendGroupMember";
    public const string AcceptFriendGroupInvitation = "AcceptFriendGroupInvitation";
    public const string DeclineFriendGroupInvitation = "DeclineFriendGroupInvitation";

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

    /// <summary>D-186: the poll analogue of <see cref="LeaveSession"/>, on the poll surface below.</summary>
    public const string LeavePoll = "LeavePoll";
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

    /// <summary>
    /// AVATARS_DESIGN.md §3.3: the picture proxy, an A-064 transport exception outside /api/v1 and
    /// outside the OpenAPI document, alongside <see cref="ExchangeGuestLink"/>.
    /// </summary>
    public const string GetAvatarProxy = "GetAvatarProxy";
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

    // PHASE6_DESIGN.md §4: the registered poll HTTP surface, relative to /api/v1 (ticket-P6-S4a).
    public const string CreatePoll = "CreatePoll";
    public const string GetPoll = "GetPoll";
    public const string SuggestPollOption = "SuggestPollOption";
    public const string DeletePollOption = "DeletePollOption";
    public const string VoteOnPollOption = "VoteOnPollOption";
    public const string FinalizePoll = "FinalizePoll";
    public const string CancelPoll = "CancelPoll";
    public const string CreatePollGuestLinks = "CreatePollGuestLinks";
    public const string RevokePollGuestLink = "RevokePollGuestLink";
    public const string RemovePollGuest = "RemovePollGuest";
    public const string RequestPollJoin = "RequestPollJoin";
    public const string WithdrawPollJoinRequest = "WithdrawPollJoinRequest";
    public const string ListPollJoinRequests = "ListPollJoinRequests";
    public const string ApprovePollJoinRequest = "ApprovePollJoinRequest";
    public const string RejectPollJoinRequest = "RejectPollJoinRequest";

    /// <summary>D-193: the organizer's mid-flight registered-invite route on the poll surface.</summary>
    public const string InvitePollUsers = "InvitePollUsers";

    // PHASE6_DESIGN.md §4.1: reserved here (poll-list-read dimension membership only) — S4c maps
    // the routes these ids name.
    public const string ListPolls = "ListPolls";
    public const string GetPollFinalizationPreview = "GetPollFinalizationPreview";

    // PHASE6_DESIGN.md §4: the poll guest arm, relative to /api/v1 (ticket-P6-S4b) — parallel nested
    // resources on the shipped /guest/{guestLinkId} group, joining the shipped guest-write dimension.
    public const string GetGuestPollView = "GetGuestPollView";
    public const string ClaimGuestPollLink = "ClaimGuestPollLink";
    public const string VoteOnPollAsGuest = "VoteOnPollAsGuest";
    public const string SuggestPollOptionAsGuest = "SuggestPollOptionAsGuest";
    public const string RenameGuestPollIdentity = "RenameGuestPollIdentity";

    // PHASE7_DESIGN.md §4: the S4 HTTP surface -- eleven ids exactly, registered-user only (D-167).
    public const string GetPushConfig = "GetPushConfig";
    public const string CreatePushSubscription = "CreatePushSubscription";
    public const string DeletePushSubscription = "DeletePushSubscription";
    public const string ListNotifications = "ListNotifications";
    public const string GetUnreadNotificationCount = "GetUnreadNotificationCount";
    public const string MarkNotificationRead = "MarkNotificationRead";
    public const string MarkAllNotificationsRead = "MarkAllNotificationsRead";
    public const string MuteSession = "MuteSession";
    public const string UnmuteSession = "UnmuteSession";
    public const string MutePoll = "MutePoll";
    public const string UnmutePoll = "UnmutePoll";

    // TOUR_DESIGN.md §4: the first-login tour's one write.
    public const string CompleteTour = "CompleteTour";

    // D189_DESIGN.md §2/§3: the account setting and its unauthenticated unsubscribe-link confirm.
    public const string ChangeMyEmailNotifications = "ChangeMyEmailNotifications";
    public const string UnsubscribeEmailNotifications = "UnsubscribeEmailNotifications";

    // STEAM_CATALOG_DESIGN.md §3: the catalog typeahead search.
    public const string SearchSteamCatalog = "SearchSteamCatalog";
}
