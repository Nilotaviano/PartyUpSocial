namespace PartyUp.Application.Email;

public enum EmailTemplate
{
    MagicLink = 0,
    EmailVerification = 1,
    EmailChangeRequested = 2,
    EmailChangeVerification = 3,
    PasswordReset = 4,
    DeletionRequested = 5,
    DeletionCanceled = 6,
    ExistingAccountNotice = 7,
    AuthMethodAdded = 8,
    AuthMethodRemoved = 9,
    PasswordResetNoAccount = 10,
    DeletionRequestNoAccount = 11,
    ReauthenticationRequested = 12,
    DeletionRequestConfirmation = 13,
}
