using Microsoft.EntityFrameworkCore;

namespace PartyUp.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
