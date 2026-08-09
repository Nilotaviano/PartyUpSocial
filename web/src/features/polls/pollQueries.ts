import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type PollSummary = components['schemas']['PollSummaryResponse']
export type PollDetail = components['schemas']['PollDetailResponse']
export type PollOption = components['schemas']['PollOptionResponse']
export type PollRosterEntry = components['schemas']['PollRosterEntryResponse']
export type CreatePollRequest = components['schemas']['CreatePollRequest']
export type CreatePollResponse = components['schemas']['CreatePollResponse']
export type PollBand = 'involved' | 'discoverable'

export const pollKeys = {
  list: (band: PollBand, cursor?: string) =>
    ['polls', band, cursor ?? null] as const,
  listFamily: (band: PollBand) => ['polls', band] as const,
  detail: (pollId: string) => ['poll', pollId] as const,
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getPollList(band: PollBand, cursor?: string) {
  const { data } = await client.GET('/api/v1/polls', {
    params: { query: { band, cursor } },
  })
  if (!data) throw new Error('Poll list response was empty.')
  return data
}

export async function getPollDetail(pollId: string) {
  const { data } = await client.GET('/api/v1/polls/{pollId}', {
    params: { path: { pollId } },
  })
  if (!data) throw new Error('Poll detail response was empty.')
  return data
}

export async function createPoll(body: CreatePollRequest) {
  const { data } = await client.POST('/api/v1/polls', {
    params: { header: csrf },
    body,
  })
  if (!data) throw new Error('Create poll response was empty.')
  return data
}

/**
 * Same clock-inclusive gate the server applies for Finalize/preview (design
 * §4/§5): a poll is presented as closed-awaiting-selection once its deadline
 * has passed, even before the Close sweeper has materialized the status
 * column — never derived from `status === 'Closed'` alone.
 */
export function isPollAwaitingSelection(poll: Pick<PollDetail, 'status' | 'deadline'>) {
  return (
    poll.status === 'polling' && new Date(poll.deadline).getTime() <= Date.now()
  )
}
