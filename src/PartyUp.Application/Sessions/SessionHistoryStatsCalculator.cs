using PartyUp.Domain.Kernel;
using PartyUp.Domain.Users;

namespace PartyUp.Application.Sessions;

/// <summary>
/// D-222's arithmetic, isolated from both the SQL that gathers the facts and the masking that
/// renders the people — the recap is a pure fold over <see cref="CompletedSessionFact"/>s, so every
/// rounding and tie-break rule below is provable without a database.
/// </summary>
public static class SessionHistoryStatsCalculator
{
    /// <summary>The recap never names more than three people (D-222).</summary>
    public const int TopMateCap = 3;

    /// <summary>
    /// Sums first, rounds once: minutes are totalled across the whole history and only the total is
    /// rounded to whole hours, half up. Rounding each session on its own would drift by up to half an
    /// hour per row.
    /// </summary>
    public static SessionHistoryStats Compute(IReadOnlyList<CompletedSessionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        long totalMinutes = 0;
        Dictionary<string, (GameName Name, int Times)> games = new(StringComparer.Ordinal);
        Dictionary<UserId, int> mates = [];

        foreach (CompletedSessionFact fact in facts)
        {
            totalMinutes += fact.DurationMinutes;

            if (fact.Game is { } game)
            {
                (GameName Name, int Times) tally = games.GetValueOrDefault(game.Value, (game, 0));
                games[game.Value] = (tally.Name, tally.Times + 1);
            }

            foreach (UserId companion in fact.Companions.Distinct())
            {
                mates[companion] = mates.GetValueOrDefault(companion) + 1;
            }
        }

        int hoursTogether = (int)((totalMinutes + 30) / 60);

        // Deterministic tie-breaks (the D-206 ordering discipline): most times first, then the
        // game's name ordinal / the person's id — never the enumeration order of a dictionary.
        HistoryTopGame? topGame = games.Count == 0
            ? null
            : games.Values
                .OrderByDescending(entry => entry.Times)
                .ThenBy(entry => entry.Name.Value, StringComparer.Ordinal)
                .Select(entry => new HistoryTopGame(entry.Name, entry.Times))
                .First();

        List<HistoryTopMate> topMates = [.. mates
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key.Value)
            .Take(TopMateCap)
            .Select(entry => new HistoryTopMate(new HistoryPlayer(entry.Key), entry.Value))];

        return new SessionHistoryStats(facts.Count, hoursTogether, topGame, topMates);
    }
}
