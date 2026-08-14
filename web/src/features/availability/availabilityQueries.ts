import type { QueryClient } from '@tanstack/react-query'
import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type MyAvailabilityLayer = components['schemas']['MyAvailabilityLayerResponse']
export type CalendarView = components['schemas']['CalendarViewResponse']
export type OverlapSearchResult = components['schemas']['OverlapSearchResponse']
export type HomeSuggestions = components['schemas']['HomeSuggestionsResponse']
export type AvailabilityBlockView = components['schemas']['AvailabilityBlockView']
export type AvailabilitySeriesView = components['schemas']['AvailabilitySeriesView']
export type EditOccurrenceRequest = components['schemas']['EditOccurrenceRequest']
export type EditOccurrenceResponse = components['schemas']['EditOccurrenceResponse']
export type CreateAvailabilityBlockRequest =
  components['schemas']['CreateAvailabilityBlockRequest']
export type EditAvailabilityBlockRequest =
  components['schemas']['EditAvailabilityBlockRequest']
export type CreateAvailabilitySeriesRequest =
  components['schemas']['CreateAvailabilitySeriesRequest']
export type MaskedParticipant = components['schemas']['MaskedParticipant']
export type OverlapWindowView = components['schemas']['OverlapWindowView']

/**
 * Every availability query key lives here — S6-exclusive (design §10.1).
 * Layer visibility is component state and never enters a key
 * (D-135); ['calendar', from, to] carries every layer for one range so the
 * week grid and agenda never disagree mid-render (§5.1).
 */
export const availabilityKeys = {
  me: (from: string, to: string) => ['availability', 'me', from, to] as const,
  overlaps: (friendIds: string[], from: string, to: string) =>
    ['availability', 'overlaps', friendIds, from, to] as const,
  suggestions: () => ['availability', 'suggestions'] as const,
  calendar: (from: string, to: string) => ['calendar', from, to] as const,
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getMyAvailability(from: string, to: string) {
  const { data } = await client.GET('/api/v1/me/availability', {
    params: { query: { from, to } },
  })
  if (!data) throw new Error('Availability response was empty.')
  return data
}

export async function getCalendarView(from: string, to: string) {
  const { data } = await client.GET('/api/v1/calendar', {
    params: { query: { from, to } },
  })
  if (!data) throw new Error('Calendar response was empty.')
  return data
}

export async function findOverlapWindows(
  friendIds: string[] | undefined,
  from: string,
  to: string,
) {
  const { data } = await client.GET('/api/v1/availability/overlaps', {
    params: {
      query: {
        friendIds: friendIds && friendIds.length > 0 ? friendIds.join(',') : undefined,
        from,
        to,
      },
    },
  })
  if (!data) throw new Error('Overlap search response was empty.')
  return data
}

export async function getHomeSuggestions() {
  const { data } = await client.GET('/api/v1/availability/suggestions')
  if (!data) throw new Error('Suggestions response was empty.')
  return data
}

export async function createAvailabilityBlock(body: CreateAvailabilityBlockRequest) {
  const { data } = await client.POST('/api/v1/me/availability/blocks', {
    params: { header: csrf },
    body,
  })
  if (!data) throw new Error('Create block response was empty.')
  return data
}

export async function editAvailabilityBlock(
  blockId: string,
  body: EditAvailabilityBlockRequest,
) {
  const { data } = await client.PATCH('/api/v1/me/availability/blocks/{blockId}', {
    params: { header: csrf, path: { blockId } },
    body,
  })
  if (!data) throw new Error('Edit block response was empty.')
  return data
}

export async function deleteAvailabilityBlock(blockId: string) {
  await client.DELETE('/api/v1/me/availability/blocks/{blockId}', {
    params: { header: csrf, path: { blockId } },
  })
}

export async function createAvailabilitySeries(body: CreateAvailabilitySeriesRequest) {
  const { data } = await client.POST('/api/v1/me/availability/series', {
    params: { header: csrf },
    body,
  })
  if (!data) throw new Error('Create series response was empty.')
  return data
}

export async function deleteAvailabilitySeries(seriesId: string) {
  await client.DELETE('/api/v1/me/availability/series/{seriesId}', {
    params: { header: csrf, path: { seriesId } },
  })
}

export async function editAvailabilityOccurrence(
  seriesId: string,
  occurrenceDate: string,
  body: EditOccurrenceRequest,
) {
  const { data } = await client.PATCH(
    '/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}',
    {
      params: { header: csrf, path: { seriesId, occurrenceDate } },
      body,
    },
  )
  if (!data) throw new Error('Edit occurrence response was empty.')
  return data
}

export async function deleteAvailabilityOccurrence(
  seriesId: string,
  occurrenceDate: string,
) {
  await client.DELETE(
    '/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}',
    { params: { header: csrf, path: { seriesId, occurrenceDate } } },
  )
}

/**
 * Every availability mutation invalidates the first four query families
 * (design §10.1) — `['availability','me']`, `['availability','overlaps']`,
 * `['availability','suggestions']` and `['calendar']` — since a block/series
 * write can change every one of them.
 */
export async function invalidateAvailabilityQueries(queryClient: QueryClient) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: ['availability', 'me'] }),
    queryClient.invalidateQueries({ queryKey: ['availability', 'overlaps'] }),
    queryClient.invalidateQueries({ queryKey: ['availability', 'suggestions'] }),
    queryClient.invalidateQueries({ queryKey: ['calendar'] }),
  ])
}
