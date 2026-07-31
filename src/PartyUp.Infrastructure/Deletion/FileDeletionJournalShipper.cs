using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodaTime;
using NodaTime.Text;
using PartyUp.Application.Abstractions;
using PartyUp.Infrastructure.Persistence;

namespace PartyUp.Infrastructure.Deletion;

public sealed partial class FileDeletionJournalShipper(
    AppDbContext context,
    DeletionJournalOptions options,
    IClock clock,
    ILogger<FileDeletionJournalShipper> logger) : IDeletionJournalShipper
{
    public async Task<int> ShipPendingAsync(CancellationToken cancellationToken)
    {
        string? directory = ResolveDirectory(options.Path);
        if (directory is null)
        {
            LogUnavailablePath(logger, options.Path);
            return 0;
        }

        Directory.CreateDirectory(directory);
        List<DeletionJournalRecord> pending = await context.DeletionJournal
            .Where(entry => entry.ShippedAt == null)
            .OrderBy(entry => entry.ExecutedAt)
            .ToListAsync(cancellationToken);
        int shipped = 0;
        foreach (DeletionJournalRecord entry in pending)
        {
            string file = Path.Combine(
                directory,
                FormattableString.Invariant(
                    $"{entry.ExecutedAt.InUtc().Year:0000}-{entry.ExecutedAt.InUtc().Month:00}.ndjson"));
            if (!await LastEntryMatchesAsync(file, entry, cancellationToken))
            {
                string line = JsonSerializer.Serialize(new
                {
                    userId = entry.UserId.Value,
                    executedAt = InstantPattern.ExtendedIso.Format(entry.ExecutedAt),
                }) + Environment.NewLine;
                await AppendDurablyAsync(file, line, cancellationToken);
            }

            entry.ShippedAt = clock.GetCurrentInstant();
            await context.SaveChangesAsync(cancellationToken);
            shipped++;
        }

        return shipped;
    }

    private static string? ResolveDirectory(string configured)
    {
        if (OperatingSystem.IsWindows() && configured.Length > 0 && configured[0] == '/')
        {
            return null;
        }

        string fullPath = Path.GetFullPath(configured);
        string? parent = Path.GetDirectoryName(fullPath);
        return parent is not null && Directory.Exists(parent) ? fullPath : null;
    }

    private static async Task<bool> LastEntryMatchesAsync(
        string file,
        DeletionJournalRecord entry,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        string? last = null;
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 0)
            {
                last = line;
            }
        }

        if (last is null)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(last);
        return document.RootElement.TryGetProperty("userId", out JsonElement userId) &&
            userId.TryGetGuid(out Guid value) &&
            value == entry.UserId.Value;
    }

    private static async Task AppendDurablyAsync(
        string file,
        string line,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        await using var stream = new FileStream(
            file,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Deletion journal path {Path} is unavailable; file shipping is disabled")]
    private static partial void LogUnavailablePath(ILogger logger, string path);
}

public sealed record DeletionJournalOptions(string Path);
