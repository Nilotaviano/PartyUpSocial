namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// DOMAIN_MODEL.md rules 1-4 as one SQL fragment (A-077 (a)), extracted from the four copies
/// previously inlined across <c>SessionReadRepository.ListUpcomingAsync</c>/<c>ListHistoryAsync</c>
/// (cursor and no-cursor branches of each). The text uses the positional placeholder <c>{0}</c> for
/// the viewer's <c>UserId</c>; a caller splices it into a larger composed query with
/// <c>System.Runtime.CompilerServices.FormattableStringFactory.Create</c>, assigning <c>{0}</c> to
/// the viewer argument, so the fragment stays a genuine EF-parameterized predicate rather than a
/// string-embedded literal.
///
/// The rule-2g arm (<c>visibility = 2</c>, D-198/D-203) intentionally carries no per-extender block
/// check: rule 4 (viewer↔organizer) is the only block dimension a group grant has, and an
/// owner↔member block can never reach here because the block effects deleted the membership row.
/// </summary>
public static class SessionVisibilitySql
{
    public enum AccessKind
    {
        /// <summary>Rules 1-4 in full: organizer, a pending join request, a participation row, or
        /// the rule-2 inherited path — minus rule 4's blocked pair.</summary>
        Any,

        /// <summary>Rule 2's inherited path only — "stated purely over the extenders' rows"
        /// (A-077 (d)) — plus rule 2g's group audience, minus rule 4's blocked pair.</summary>
        InheritedOnly,
    }

    public static string Predicate(AccessKind accessKind) => accessKind switch
    {
        AccessKind.Any => """
            (
                NOT EXISTS (
                    SELECT 1 FROM blocks b
                    WHERE (b.blocker_id = {0} AND b.blocked_id = s.organizer_id)
                       OR (b.blocker_id = s.organizer_id AND b.blocked_id = {0})
                )
                AND (
                    s.organizer_id = {0}
                    OR EXISTS (
                        SELECT 1 FROM join_requests jr
                        WHERE jr.session_id = s.id AND jr.requester_user_id = {0} AND jr.status = 0
                    )
                    OR EXISTS (
                        SELECT 1 FROM session_participations sp
                        WHERE sp.session_id = s.id AND sp.user_id = {0} AND sp.status <> 4
                          AND (sp.origin = 0 OR sp.status IN (0, 1, 2))
                    )
                    OR (
                        s.visibility = 0
                        AND (
                            EXISTS (
                                SELECT 1 FROM friendships f
                                WHERE f.status = 1
                                  AND ((f.user_lo_id = {0} AND f.user_hi_id = s.organizer_id)
                                    OR (f.user_hi_id = {0} AND f.user_lo_id = s.organizer_id))
                            )
                            OR EXISTS (
                                SELECT 1 FROM session_participations sp2
                                JOIN friendships f2
                                  ON f2.status = 1
                                 AND ((f2.user_lo_id = {0} AND f2.user_hi_id = sp2.user_id)
                                   OR (f2.user_hi_id = {0} AND f2.user_lo_id = sp2.user_id))
                                WHERE sp2.session_id = s.id
                                  AND sp2.user_id IS NOT NULL
                                  AND sp2.status IN (0, 1, 2)
                                  AND NOT EXISTS (
                                      SELECT 1 FROM blocks b2
                                      WHERE (b2.blocker_id = {0} AND b2.blocked_id = sp2.user_id)
                                         OR (b2.blocker_id = sp2.user_id AND b2.blocked_id = {0})
                                  )
                            )
                        )
                    )
                    OR (
                        s.visibility = 2
                        AND EXISTS (
                            SELECT 1 FROM session_audience_groups sag
                            JOIN friend_groups fg ON fg.id = sag.group_id
                            WHERE sag.session_id = s.id
                              AND (fg.owner_user_id = {0}
                                   OR EXISTS (
                                       SELECT 1 FROM friend_group_members fgm
                                       WHERE fgm.group_id = sag.group_id AND fgm.user_id = {0} AND fgm.status = 1
                                   ))
                        )
                    )
                )
            )
            """,
        AccessKind.InheritedOnly => """
            (
                NOT EXISTS (
                    SELECT 1 FROM blocks b
                    WHERE (b.blocker_id = {0} AND b.blocked_id = s.organizer_id)
                       OR (b.blocker_id = s.organizer_id AND b.blocked_id = {0})
                )
                AND (
                    (
                        s.visibility = 0
                        AND (
                            EXISTS (
                                SELECT 1 FROM friendships f
                                WHERE f.status = 1
                                  AND ((f.user_lo_id = {0} AND f.user_hi_id = s.organizer_id)
                                    OR (f.user_hi_id = {0} AND f.user_lo_id = s.organizer_id))
                            )
                            OR EXISTS (
                                SELECT 1 FROM session_participations sp2
                                JOIN friendships f2
                                  ON f2.status = 1
                                 AND ((f2.user_lo_id = {0} AND f2.user_hi_id = sp2.user_id)
                                   OR (f2.user_hi_id = {0} AND f2.user_lo_id = sp2.user_id))
                                WHERE sp2.session_id = s.id
                                  AND sp2.user_id IS NOT NULL
                                  AND sp2.status IN (0, 1, 2)
                                  AND NOT EXISTS (
                                      SELECT 1 FROM blocks b2
                                      WHERE (b2.blocker_id = {0} AND b2.blocked_id = sp2.user_id)
                                         OR (b2.blocker_id = sp2.user_id AND b2.blocked_id = {0})
                                  )
                            )
                        )
                    )
                    OR (
                        s.visibility = 2
                        AND EXISTS (
                            SELECT 1 FROM session_audience_groups sag
                            JOIN friend_groups fg ON fg.id = sag.group_id
                            WHERE sag.session_id = s.id
                              AND (fg.owner_user_id = {0}
                                   OR EXISTS (
                                       SELECT 1 FROM friend_group_members fgm
                                       WHERE fgm.group_id = sag.group_id AND fgm.user_id = {0} AND fgm.status = 1
                                   ))
                        )
                    )
                )
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(accessKind)),
    };
}
