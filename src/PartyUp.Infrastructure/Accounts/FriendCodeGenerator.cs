using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PartyUp.Application.Abstractions;
using PartyUp.Domain.Users;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Accounts;

public sealed class FriendCodeGenerator(AppDbContext context) : IFriendCodeGenerator
{
    private const int MaxAttempts = 5;
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public async Task<FriendCode> NextAsync(CancellationToken cancellationToken)
    {
        char[] characters = new char[10];
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            byte[] entropy = RandomNumberGenerator.GetBytes(10);
            for (int index = 0; index < characters.Length; index++)
            {
                characters[index] = Alphabet[entropy[index] & 31];
            }

            FriendCode candidate = FriendCode.Create(new string(characters));
            bool exists = await context.DomainUsers
                .AsNoTracking()
                .AnyAsync(user => user.FriendCode == candidate, cancellationToken);
            if (!exists)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "A unique friend code could not be generated after five attempts.");
    }
}
