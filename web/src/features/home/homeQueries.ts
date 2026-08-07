import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type ActionableInbox = components['schemas']['ActionableInboxResponse']
export type ActionableInvitation =
  components['schemas']['ActionableInvitationResponse']
export type ActionableJoinRequest =
  components['schemas']['ActionableJoinRequestResponse']
export type DiscoverSessionPage =
  components['schemas']['SessionPageResponseOfSessionSummaryResponse']

/**
 * S7's own query keys (design §10.1) — the only two hooks this slice
 * authors. Every availability key (`['availability','suggestions']`,
 * `['availability','me']`, …) is S6's, imported from `availabilityQueries.ts`
 * rather than redeclared here.
 */
export const homeKeys = {
  actionable: ['home', 'actionable'] as const,
  discover: (cursor?: string) =>
    ['sessions', 'discover', cursor ?? null] as const,
}

export async function getActionableInbox() {
  const { data } = await client.GET('/api/v1/me/actionable')
  if (!data) throw new Error('Actionable inbox response was empty.')
  return data
}

export async function getDiscoverSessions(cursor?: string) {
  const { data } = await client.GET('/api/v1/sessions', {
    params: { query: { scope: 'discover', cursor } },
  })
  if (!data) throw new Error('Discover session response was empty.')
  return data
}
