using NodaTime;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Profile;

/// <summary>
/// D-061/A-081: the zone migration is a step inside this use case's existing transaction, under the
/// <c>users</c> lock <see cref="IUserRepository.FindForUpdateAsync"/> already takes — not a job, not a
/// second command, not a new port. Every <c>AvailabilitySeries.ZoneId</c> the owner has moves to the
/// new tzdb id in the same <c>UPDATE</c>; one-time and materialized edit-one blocks are absolute
/// instants and are provably untouched (§12-21) — this class never calls a block-touching member of
/// <see cref="IAvailabilityRepository"/>. A failure rolls the profile change back with it.
/// </summary>
public sealed class ChangeTimeZoneUseCase(
    IUserRepository users,
    IAvailabilityRepository availability,
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

                // A-081: LockOwnerSeriesAsync then a single series-only UPDATE, inside this same
                // transaction and under the users lock already taken above. Runs before the profile
                // mutation below so a profile-side domain rejection (e.g. a non-Active lifecycle)
                // proves the migration rolls back with it, not just that it commits with it.
                await availability.LockOwnerSeriesAsync(userId, ct);
                await availability.RetargetSeriesZoneAsync(userId, timeZone.Id, ct);

                user.ChangeTimeZone(timeZone);

                return ProfileMutationResult.Completed;
            },
            cancellationToken);
    }
}
