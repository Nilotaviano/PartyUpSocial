import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type GuestSessionView = components['schemas']['GuestSessionResponse']
export type GuestRsvpResponse = 'Going' | 'Maybe' | 'Declined'

/**
 * Design §10.1: the guest query key. There is no separate roster/list key —
 * a guest link resolves to exactly one session view or terminal notice.
 */
export const guestSessionKeys = {
  detail: (guestLinkId: string) => ['guest-session', guestLinkId] as const,
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getGuestSession(guestLinkId: string) {
  const { data } = await client.GET('/api/v1/guest/{guestLinkId}/session', {
    params: { path: { guestLinkId } },
  })
  if (!data) throw new Error('Guest session response was empty.')
  return data
}

export async function claimGuestSession(
  guestLinkId: string,
  displayName: string,
  response: GuestRsvpResponse | 'Pending',
) {
  const { data } = await client.POST('/api/v1/guest/{guestLinkId}/claim', {
    params: { header: csrf, path: { guestLinkId } },
    body: { displayName, response },
  })
  if (!data) throw new Error('Guest claim response was empty.')
  return data
}

export async function renameGuestSession(
  guestLinkId: string,
  displayName: string,
) {
  const { data } = await client.PUT(
    '/api/v1/guest/{guestLinkId}/display-name',
    {
      params: { header: csrf, path: { guestLinkId } },
      body: { displayName },
    },
  )
  if (!data) throw new Error('Guest rename response was empty.')
  return data
}

export async function respondAsGuest(
  guestLinkId: string,
  response: GuestRsvpResponse,
) {
  const { data } = await client.PUT('/api/v1/guest/{guestLinkId}/participation', {
    params: { header: csrf, path: { guestLinkId } },
    body: { response },
  })
  if (!data) throw new Error('Guest response was empty.')
  return data
}

/**
 * D-225: the claimed seat moves to the signed-in account. The guest cookie is the proof of
 * "I am that guest", so the call carries no body — the account comes from the auth cookie.
 */
export async function upgradeGuestSession(guestLinkId: string) {
  const { data } = await client.POST('/api/v1/guest/{guestLinkId}/upgrade', {
    params: { header: csrf, path: { guestLinkId } },
  })
  if (!data) throw new Error('Guest upgrade response was empty.')
  return data
}

/**
 * Terminal states (design §8.1/A-074): the guest link/session is no longer
 * live. `pollExpired` is a Poll-domain notice (`GuestLinkView.PollExpiredNotice`)
 * that a fixed-time session guest link can never resolve to (§12 interpretation:
 * `GuestLinkView.ForSession` never returns it), so it maps to the same fallback
 * as an unrecognized state rather than claiming a dedicated locale key.
 */
export function guestTerminalLocaleKey(state: string): string {
  switch (state) {
    case 'canceled':
      return 'guest.canceled'
    case 'revoked':
      return 'guest.revoked'
    case 'ended':
      return 'guest.ended'
    default:
      return 'guest.invalid'
  }
}
