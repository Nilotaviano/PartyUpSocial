namespace PartyUp.Infrastructure.Persistence.Repositories;

/// <summary>
/// DOMAIN_MODEL.md §7 rules 1/2 as one SQL fragment per band, mirroring
/// <see cref="SessionVisibilitySql"/>'s extraction (PHASE6_DESIGN.md §4.1). The text uses the
/// positional placeholder <c>{0}</c> for the viewer's <c>UserId</c>, spliced by a caller with
/// <c>FormattableStringFactory.Create</c> against a query aliasing the <c>polls</c> table <c>p</c>.
///
/// The rule-2g arm (<c>visibility = 2</c>, D-198/D-203) intentionally carries no per-extender block
/// check: rule 4 (viewer↔organizer) is the only block dimension a group grant has, and an
/// owner↔member block can never reach here because the block effects deleted the membership row.
/// </summary>
public static class PollVisibilitySql
{
    public enum AccessKind
    {
        /// <summary>
        /// §7 rule 1: the organizer, a pending join requester (<c>VisibilityEngine.cs:32</c>'s
        /// <c>HasPendingRequestFrom</c> arm -- round-1 fix: the poll SQL originally omitted this,
        /// unlike <see cref="SessionVisibilitySql"/>'s own rule-1 fragment), or a registered
        /// participant row still <c>Active</c> (<c>PollVisibility.KeepsDirectAccess</c> -- a poll
        /// participant has no Pending state). Minus rule 4's blocked pair.
        /// </summary>
        DirectAccess,

        /// <summary>
        /// §7 rule 2 / §12-17: public, and either a friend of the organizer or a friend of an
        /// <c>Active</c> registered voter whose current votes are not all <c>Unavailable</c>
        /// (<c>PollVisibility.ExtendsAccess</c> -- a voter with no votes yet extends, like a pending
        /// invitee) — plus rule 2g's group audience. Minus rule 4's blocked pair.
        /// </summary>
        InheritedOnly,
    }

    public static string Predicate(AccessKind accessKind) => accessKind switch
    {
        AccessKind.DirectAccess => """
            (
                NOT EXISTS (
                    SELECT 1 FROM blocks b
                    WHERE (b.blocker_id = {0} AND b.blocked_id = p.organizer_id)
                       OR (b.blocker_id = p.organizer_id AND b.blocked_id = {0})
                )
                AND (
                    p.organizer_id = {0}
                    OR EXISTS (
                        SELECT 1 FROM join_requests jr
                        WHERE jr.poll_id = p.id AND jr.requester_user_id = {0} AND jr.status = 0
                    )
                    OR EXISTS (
                        SELECT 1 FROM poll_participants pp
                        WHERE pp.poll_id = p.id AND pp.user_id = {0} AND pp.status = 0
                    )
                )
            )
            """,
        AccessKind.InheritedOnly => """
            (
                NOT EXISTS (
                    SELECT 1 FROM blocks b
                    WHERE (b.blocker_id = {0} AND b.blocked_id = p.organizer_id)
                       OR (b.blocker_id = p.organizer_id AND b.blocked_id = {0})
                )
                AND (
                    (
                        p.visibility = 0
                        AND (
                            EXISTS (
                                SELECT 1 FROM friendships f
                                WHERE f.status = 1
                                  AND ((f.user_lo_id = {0} AND f.user_hi_id = p.organizer_id)
                                    OR (f.user_hi_id = {0} AND f.user_lo_id = p.organizer_id))
                            )
                            OR EXISTS (
                                SELECT 1 FROM poll_participants pp2
                                JOIN friendships f2
                                  ON f2.status = 1
                                 AND ((f2.user_lo_id = {0} AND f2.user_hi_id = pp2.user_id)
                                   OR (f2.user_hi_id = {0} AND f2.user_lo_id = pp2.user_id))
                                WHERE pp2.poll_id = p.id
                                  AND pp2.user_id IS NOT NULL
                                  AND pp2.status = 0
                                  AND NOT EXISTS (
                                      SELECT 1 FROM blocks b2
                                      WHERE (b2.blocker_id = {0} AND b2.blocked_id = pp2.user_id)
                                         OR (b2.blocker_id = pp2.user_id AND b2.blocked_id = {0})
                                  )
                                  AND (
                                      NOT EXISTS (
                                          SELECT 1 FROM poll_votes pv
                                          WHERE pv.poll_id = p.id AND pv.user_id = pp2.user_id
                                      )
                                      OR EXISTS (
                                          SELECT 1 FROM poll_votes pv
                                          WHERE pv.poll_id = p.id AND pv.user_id = pp2.user_id AND pv.choice <> 2
                                      )
                                  )
                            )
                        )
                    )
                    OR (
                        p.visibility = 2
                        AND EXISTS (
                            SELECT 1 FROM poll_audience_groups pag
                            JOIN friend_groups fg ON fg.id = pag.group_id
                            WHERE pag.poll_id = p.id
                              AND (fg.owner_user_id = {0}
                                   OR EXISTS (
                                       SELECT 1 FROM friend_group_members fgm
                                       WHERE fgm.group_id = pag.group_id AND fgm.user_id = {0} AND fgm.status = 1
                                   ))
                        )
                    )
                )
            )
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(accessKind)),
    };
}
