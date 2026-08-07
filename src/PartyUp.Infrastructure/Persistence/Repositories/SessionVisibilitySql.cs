namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// DOMAIN_MODEL.md rules 1-4 as one SQL fragment (A-077 (a)), extracted from the four copies
/// previously inlined across <c>SessionReadRepository.ListUpcomingAsync</c>/<c>ListHistoryAsync</c>
/// (cursor and no-cursor branches of each). The text uses the positional placeholder <c>{0}</c> for
/// the viewer's <c>UserId</c>; a caller splices it into a larger composed query with
/// <c>System.Runtime.CompilerServices.FormattableStringFactory.Create</c>, assigning <c>{0}</c> to
/// the viewer argument, so the fragment stays a genuine EF-parameterized predicate rather than a
/// string-embedded literal.
/// </summary>
public static class SessionVisibilitySql
{
    public enum AccessKind
    {
        /// <summary>Rules 1-4 in full: organizer, a pending join request, a participation row, or
        /// the rule-2 inherited path — minus rule 4's blocked pair.</summary>
        Any,

        /// <summary>Rule 2's inherited path only — "stated purely over the extenders' rows"
        /// (A-077 (d)) — minus rule 4's blocked pair. No S2 caller; proven at <c>P5-VIS-01</c> (c), S4's.</summary>
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
                        NOT s.is_private
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
                AND NOT s.is_private
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
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(accessKind)),
    };
}
