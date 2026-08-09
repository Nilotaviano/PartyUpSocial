import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type PollSummary = components['schemas']['PollSummaryResponse']
export type PollDetail = components['schemas']['PollDetailResponse']
export type PollOption = components['schemas']['PollOptionResponse']
export type PollRosterEntry = components['schemas']['PollRosterEntryResponse']
export type CreatePollRequest = components['schemas']['CreatePollRequest']
export type CreatePollResponse = components['schemas']['CreatePollResponse']
export type PollBand = 'involved' | 'discoverable'
export type GuestLinkInvitation = components['schemas']['GuestLinkInvitationResponse']
export type VoteChoiceWire = 'available' | 'maybe' | 'unavailable'

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

// design §7 note 1: vote submission is per option, not per ballot.
export async function voteOnPollOption(
  pollId: string,
  optionId: string,
  choice: VoteChoiceWire,
) {
  const { data } = await client.PUT('/api/v1/polls/{pollId}/options/{optionId}/vote', {
    params: { header: csrf, path: { pollId, optionId } },
    body: { choice },
  })
  if (!data) throw new Error('Vote response was empty.')
  return data
}

export async function suggestPollOption(pollId: string, startTime: string) {
  const { data } = await client.POST('/api/v1/polls/{pollId}/options', {
    params: { header: csrf, path: { pollId } },
    body: { startTime },
  })
  if (!data) throw new Error('Suggest option response was empty.')
  return data
}

export async function createPollGuestLinks(pollId: string, count: number) {
  const { data } = await client.POST('/api/v1/polls/{pollId}/guest-links', {
    params: { header: csrf, path: { pollId } },
    body: { count },
  })
  if (!data) throw new Error('Create poll guest links response was empty.')
  return data
}

export async function revokePollGuestLink(pollId: string, guestLinkId: string) {
  await client.DELETE('/api/v1/polls/{pollId}/guest-links/{guestLinkId}', {
    params: { header: csrf, path: { pollId, guestLinkId } },
  })
}

// D-044: removing a claimed guest voter is the only detach available to the organizer —
// renaming stays with the guest on their own page.
export async function removePollGuest(pollId: string, guestIdentityId: string) {
  await client.POST('/api/v1/polls/{pollId}/guests/{guestIdentityId}/remove', {
    params: { header: csrf, path: { pollId, guestIdentityId } },
  })
}

export type PollWindow = Pick<
  PollDetail | PollSummary,
  'status' | 'deadline' | 'selectionWindowEndsAt'
>

/**
 * The clock-inclusive presentation gate for a poll's post-deadline life
 * (design §4/§5/§6.1), covering the whole span from deadline to
 * `selectionWindowEndsAt` — not just the ≤60s window before the minutely
 * Close sweeper materializes `status`. `status` still lags the clock by up
 * to a sweep interval in both directions (Close at the deadline, Expire at
 * `selectionWindowEndsAt`), so this reads the clock first and only falls
 * back to `status` for the states no clock field distinguishes
 * (finalized/expired/canceled are all terminal and clock-independent):
 *
 * - `'polling'`: `status` is `polling` and `now < deadline` — ordinary voting.
 * - `'awaitingSelection'`: `status ∈ {polling, closed}`, `deadline ≤ now <
 *   selectionWindowEndsAt` — voting has closed, the organizer has not yet
 *   finalized, and there is still time for them to.
 * - `'expiredPending'`: `status ∈ {polling, closed}`, `now ≥
 *   selectionWindowEndsAt` — the selection window itself has closed; the
 *   Expire sweeper has not yet materialized `status: expired`, but "the
 *   organizer is choosing" is no longer true, so this is presented
 *   distinctly rather than folded into `awaitingSelection`.
 * - `null`: `status` is already terminal (`finalized`/`expired`/`canceled`) —
 *   the caller renders directly from `status` in that case.
 */
export function pollWindowState(
  poll: PollWindow,
): 'polling' | 'awaitingSelection' | 'expiredPending' | null {
  if (poll.status !== 'polling' && poll.status !== 'closed') return null

  const now = Date.now()
  if (now >= new Date(poll.selectionWindowEndsAt).getTime()) return 'expiredPending'
  if (poll.status === 'closed' || now >= new Date(poll.deadline).getTime()) {
    return 'awaitingSelection'
  }
  return 'polling'
}

export function isPollAwaitingSelection(poll: PollWindow) {
  return pollWindowState(poll) === 'awaitingSelection'
}

export function isPollExpiredPending(poll: PollWindow) {
  return pollWindowState(poll) === 'expiredPending'
}
