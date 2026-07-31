namespace PartyUp.Application.Auth;

public enum AuthTokenPurpose
{
    MagicLink = 0,
    PasswordReset = 1,
    EmailVerification = 2,
    EmailChange = 3,
    DeletionRequestConfirmation = 4,
    ExternalStartIntent = 5,
    Reauthentication = 6,
}