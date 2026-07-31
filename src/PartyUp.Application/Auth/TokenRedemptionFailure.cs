namespace PartyUp.Application.Auth;

public enum TokenRedemptionFailure
{
    NotFound = 0,
    Expired = 1,
    AlreadyConsumed = 2,
    RequiresAgeGate = 3,
}