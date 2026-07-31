using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

public sealed class ChangeTimeZoneUseCase(
    IUserRepository users,
    IUnitOfWork unitOfWork,
    IDateTimeZoneProvider timeZones)
{
    public Task<ProfileMutationResult> ExecuteAsync(
        UserId userId,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        DateTimeZone? timeZone = timeZones.GetZoneOrNull(timeZoneId?.Trim() ?? string.Empty);
        if (timeZone is null)
        {
            return Task.FromResult(ProfileMutationResult.Failed("invalid_time_zone"));
        }

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                User? user = await users.FindForUpdateAsync(userId, ct);
                if (user is null)
                {
                    return ProfileMutationResult.Failed("not_found");
                }

                user.ChangeTimeZone(timeZone);
                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
}
