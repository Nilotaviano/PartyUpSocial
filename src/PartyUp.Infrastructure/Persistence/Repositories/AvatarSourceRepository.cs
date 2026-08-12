using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Kernel;

namespace PartyUp.Infrastructure.Persistence.Repositories;

public sealed class AvatarSourceRepository(AppDbContext context) : IAvatarSourceRepository
{
    public Task DeleteForUserAsync(UserId userId, CancellationToken cancellationToken) =>
        context.AvatarSources
            .Where(source => source.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

    public Task<AvatarPicture?> FindByTokenAsync(string token, CancellationToken cancellationToken) =>
        context.AvatarSources
            .AsNoTracking()
            .Where(source => source.Token == token)
            .Select(source => new AvatarPicture(source.Bytes, source.ContentType))
            .FirstOrDefaultAsync(cancellationToken);
}
