namespace PartyUp.Application.Abstractions;

public interface ICurrentUserAccessor
{
    Task<CurrentUser?> GetAsync(CancellationToken cancellationToken);
}
