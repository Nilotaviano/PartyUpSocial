import type { components } from '../../api/schema'
import { client } from '../../api/client'
import type { VoteChoiceWire } from './pollQueries'

export type GuestPollView = components['schemas']['GuestPollResponse']

export const guestPollKeys = {
  detail: (guestLinkId: string) => ['guest-poll', guestLinkId] as const,
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getGuestPoll(guestLinkId: string) {
  const { data } = await client.GET('/api/v1/guest/{guestLinkId}/poll', {
    params: { path: { guestLinkId } },
  })
  if (!data) throw new Error('Guest poll response was empty.')
  return data
}

export async function claimGuestPoll(guestLinkId: string, displayName: string) {
  const { data } = await client.POST('/api/v1/guest/{guestLinkId}/poll/claim', {
    params: { header: csrf, path: { guestLinkId } },
    body: { displayName },
  })
  if (!data) throw new Error('Guest poll claim response was empty.')
  return data
}

export async function voteOnPollAsGuest(
  guestLinkId: string,
  optionId: string,
  choice: VoteChoiceWire,
) {
  const { data } = await client.PUT(
    '/api/v1/guest/{guestLinkId}/poll/options/{optionId}/vote',
    {
      params: { header: csrf, path: { guestLinkId, optionId } },
      body: { choice },
    },
  )
  if (!data) throw new Error('Guest vote response was empty.')
  return data
}

// D-211: an option carries its own length, so suggesting one has to send it.
export async function suggestPollOptionAsGuest(
  guestLinkId: string,
  startTime: string,
  durationMinutes: number,
) {
  const { data } = await client.POST('/api/v1/guest/{guestLinkId}/poll/options', {
    params: { header: csrf, path: { guestLinkId } },
    body: { startTime, durationMinutes },
  })
  if (!data) throw new Error('Guest suggest option response was empty.')
  return data
}

/** D-225: the poll mirror of `upgradeGuestSession` — the claimed seat moves to the account. */
export async function upgradeGuestPoll(guestLinkId: string) {
  const { data } = await client.POST('/api/v1/guest/{guestLinkId}/poll/upgrade', {
    params: { header: csrf, path: { guestLinkId } },
  })
  if (!data) throw new Error('Guest poll upgrade response was empty.')
  return data
}

export async function renameGuestPollIdentity(guestLinkId: string, displayName: string) {
  const { data } = await client.PUT('/api/v1/guest/{guestLinkId}/poll/display-name', {
    params: { header: csrf, path: { guestLinkId } },
    body: { displayName },
  })
  if (!data) throw new Error('Guest rename response was empty.')
  return data
}

/**
 * design §5.4/GuestPollContracts: terminal notice states, mirroring the shipped session-guest
 * fallback (`guestTerminalLocaleKey`). `finalized` is this arm's own addition — it carries no
 * re-exchange URL (the server never stores the bearer's raw token), so the client renders its
 * own "re-open your invitation link" copy from `state:"finalized"` alone.
 */
export function guestPollTerminalLocaleKey(state: string): string {
  switch (state) {
    case 'canceled':
      return 'guestPoll.canceled'
    case 'revoked':
      return 'guestPoll.revoked'
    case 'ended':
      return 'guestPoll.ended'
    case 'pollExpired':
      return 'guestPoll.expired'
    case 'finalized':
      return 'guestPoll.finalized'
    default:
      return 'guestPoll.invalid'
  }
}
