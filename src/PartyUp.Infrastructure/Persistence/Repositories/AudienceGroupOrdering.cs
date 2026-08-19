using System.Reflection;
using PartyUp.Domain.Polls;
using PartyUp.Domain.Sessions;

namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// The owned <c>_audienceGroups</c> collection Poll/Session map via <c>OwnsMany</c> has no ORDER BY
/// on its load — EF Core's owned-collection eager load is a plain JOIN with no natural row order, so
/// a page query batching many aggregates and a single-row detail query can materialize the same
/// aggregate's rows in different orders. That divergence leaks straight through
/// <c>Poll.AudienceGroupIds</c>/<c>Session.AudienceGroupIds</c> into <c>GroupProvenance</c>'s "first
/// match wins" tie-break (<c>GroupProvenanceReader</c> for list pages, and the
/// <c>PollReadUseCases</c>/<c>SessionReadUseCases</c> detail-read twin), so a viewer in two of a
/// target's audience groups could see a different group named depending on which page they read.
/// <para>
/// EF Core has no supported way to add an ORDER BY to an owned collection's automatic eager load —
/// filtered-include ordering only composes over a compiled member-access navigation, and
/// <c>_audienceGroups</c> is a private field with no public navigation property to reference from
/// here (confirmed: composing <c>Include(e =&gt; EF.Property&lt;List&lt;T&gt;&gt;(e,
/// "_audienceGroups").OrderBy(...))</c> throws at query-translation time). Sorting the already
/// materialized backing list in place, right after each load, is query-side (no schema/migration
/// involved). Every order-consuming reader sees the same ascending-by-id order: the detail/write
/// repositories normalize here after each load, and <c>GroupProvenanceReader</c> applies its own
/// matching OrderBy. Loaders that never consume audience order (block/deletion scope providers,
/// list projections that drop the collection) are deliberately not routed through this class. Group id, not group name, is the sort key: it is stable, requires no
/// join to a mutable/renamable name, and every audience row already carries it.
/// </para>
/// </summary>
internal static class AudienceGroupOrdering
{
    private static readonly FieldInfo PollAudienceGroupsField = typeof(Poll)
        .GetField("_audienceGroups", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(Poll), "_audienceGroups");

    private static readonly FieldInfo SessionAudienceGroupsField = typeof(Session)
        .GetField("_audienceGroups", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(Session), "_audienceGroups");

    public static void Normalize(Poll? poll)
    {
        if (poll is null)
        {
            return;
        }

        if (PollAudienceGroupsField.GetValue(poll) is List<PollAudienceGroup> groups)
        {
            groups.Sort((left, right) => left.GroupId.Value.CompareTo(right.GroupId.Value));
        }
    }

    public static void Normalize(IEnumerable<Poll> polls)
    {
        foreach (Poll poll in polls)
        {
            Normalize(poll);
        }
    }

    public static void Normalize(Session? session)
    {
        if (session is null)
        {
            return;
        }

        if (SessionAudienceGroupsField.GetValue(session) is List<SessionAudienceGroup> groups)
        {
            groups.Sort((left, right) => left.GroupId.Value.CompareTo(right.GroupId.Value));
        }
    }

    public static void Normalize(IEnumerable<Session> sessions)
    {
        foreach (Session session in sessions)
        {
            Normalize(session);
        }
    }
}
