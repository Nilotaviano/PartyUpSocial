/**
 * The roster API (`FriendGroupMemberResponse`) ships only a bare `userId` —
 * no name/avatar projection (see `GroupBits.tsx`'s file header for why).
 * Every group page resolves identity locally through one of these lookups
 * before rendering an `AvatarDisc`/`IdentityRow`.
 */
export type ResolvedIdentity = {
  displayName: string
  emoji?: string | null
  imgUrl?: string | null
}

/** Returns `undefined` when the caller has no local record of `userId`. */
export type IdentityLookup = (userId: string) => ResolvedIdentity | undefined

/**
 * The display name a roster/invite/owner row shows for `userId` — the
 * resolver's fallback for an id it can't answer (an unfriended member
 * surviving in the roster, per design §2's "membership survives
 * unfriending").
 */
export function identityName(
  userId: string,
  resolve: IdentityLookup,
  t: (key: string, options?: Record<string, unknown>) => string,
): string {
  return resolve(userId)?.displayName ?? t('member.unknown')
}
