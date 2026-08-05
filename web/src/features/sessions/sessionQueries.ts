import type { QueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import type { components } from '../../api/schema'
import { client, isApiError } from '../../api/client'

export type ViewerProfile = components['schemas']['MyProfileResponse']
export type SessionSummary = components['schemas']['SessionSummaryResponse']
export type SessionDetail = components['schemas']['SessionDetailResponse']
export type RosterEntry = components['schemas']['RosterEntryResponse']
export type JoinRequestView = components['schemas']['JoinRequestViewResponse']
export type ParticipationView =
  components['schemas']['ParticipationViewResponse']
export type GuestLinkInvitation =
  components['schemas']['GuestLinkInvitationResponse']
export type CreateSessionRequest = components['schemas']['CreateSessionRequest']
export type CreateSessionResponse =
  components['schemas']['CreateSessionResponse']
export type EditSessionRequest = components['schemas']['EditSessionRequest']
export type ClearField = components['schemas']['ClearFieldRequest']
export type SessionScope = 'upcoming' | 'history'
export type ParticipationResponse = 'Going' | 'Maybe' | 'Declined'
export type ReminderChoiceValue =
  | 'Off'
  | 'Minutes15'
  | 'Minutes30'
  | 'Hours1'
  | 'Hours3'
  | 'Hours12'
  | 'Hours24'
  | null

export const sessionKeys = {
  list: (scope: SessionScope, cursor?: string) =>
    ['sessions', scope, cursor ?? null] as const,
  listFamily: (scope: SessionScope) => ['sessions', scope] as const,
  detail: (sessionId: string) => ['session', sessionId] as const,
  joinRequests: (sessionId: string) =>
    ['session', 'join-requests', sessionId] as const,
}

/**
 * Same query key and endpoint as the profile settings page's own query
 * (`web/src/features/profile/ProfileSettingsPage.tsx`), so both share one
 * cache entry rather than a second `/me` fetch (D-038/D-046: times render
 * in the viewer's profile zone, not the browser zone).
 */
export const viewerProfileKey = ['my-profile'] as const

export async function getViewerProfile() {
  const { data } = await client.GET('/api/v1/me')
  if (!data) throw new Error('Profile response was empty.')
  return data
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function getSessionList(scope: SessionScope, cursor?: string) {
  const { data } = await client.GET('/api/v1/sessions', {
    params: { query: { scope, cursor } },
  })
  if (!data) throw new Error('Session list response was empty.')
  return data
}

export async function getSessionDetail(sessionId: string) {
  const { data } = await client.GET('/api/v1/sessions/{sessionId}', {
    params: { path: { sessionId } },
  })
  if (!data) throw new Error('Session detail response was empty.')
  return data
}

export async function getJoinRequests(sessionId: string) {
  const { data } = await client.GET(
    '/api/v1/sessions/{sessionId}/join-requests',
    { params: { path: { sessionId } } },
  )
  if (!data) throw new Error('Join request list response was empty.')
  return data
}

export async function createSession(body: CreateSessionRequest) {
  const { data } = await client.POST('/api/v1/sessions', {
    params: { header: csrf },
    body,
  })
  if (!data) throw new Error('Create session response was empty.')
  return data
}

async function patchSession(sessionId: string, body: EditSessionRequest) {
  const { data } = await client.PATCH('/api/v1/sessions/{sessionId}', {
    params: { header: csrf, path: { sessionId } },
    body,
  })
  if (!data) throw new Error('Edit session response was empty.')
  return data
}

/**
 * A 409 session_conflict on edit gets exactly one automatic detail
 * refetch-and-retry (with the freshly reloaded ifMatch) before the error
 * is allowed to surface to the caller (design §10.1).
 */
export async function editSessionWithConflictRetry(
  queryClient: QueryClient,
  sessionId: string,
  body: EditSessionRequest,
) {
  try {
    return await patchSession(sessionId, body)
  } catch (error) {
    if (isApiError(error) && error.code === 'session_conflict') {
      const fresh = await queryClient.fetchQuery({
        queryKey: sessionKeys.detail(sessionId),
        queryFn: () => getSessionDetail(sessionId),
      })
      return await patchSession(sessionId, { ...body, ifMatch: fresh.version })
    }
    throw error
  }
}

export async function cancelSession(sessionId: string) {
  await client.DELETE('/api/v1/sessions/{sessionId}', {
    params: { header: csrf, path: { sessionId } },
  })
}

export async function inviteUsers(sessionId: string, userIds: string[]) {
  const { data } = await client.POST('/api/v1/sessions/{sessionId}/invites', {
    params: { header: csrf, path: { sessionId } },
    body: { userIds },
  })
  if (!data) throw new Error('Invite response was empty.')
  return data
}

export async function createGuestLinks(sessionId: string, count: number) {
  const { data } = await client.POST(
    '/api/v1/sessions/{sessionId}/guest-links',
    {
      params: { header: csrf, path: { sessionId } },
      body: { count },
    },
  )
  if (!data) throw new Error('Guest link response was empty.')
  return data
}

export async function revokeGuestLink(
  sessionId: string,
  guestLinkId: string,
) {
  await client.DELETE(
    '/api/v1/sessions/{sessionId}/guest-links/{guestLinkId}',
    { params: { header: csrf, path: { sessionId, guestLinkId } } },
  )
}

export async function removeGuest(
  sessionId: string,
  participationId: string,
) {
  await client.DELETE(
    '/api/v1/sessions/{sessionId}/participations/{participationId}/guest',
    { params: { header: csrf, path: { sessionId, participationId } } },
  )
}

export async function respondToInvitation(
  sessionId: string,
  response: ParticipationResponse,
) {
  const { data } = await client.PUT(
    '/api/v1/sessions/{sessionId}/my-participation',
    {
      params: { header: csrf, path: { sessionId } },
      body: { response },
    },
  )
  if (!data) throw new Error('Participation response was empty.')
  return data
}

export async function leaveSession(sessionId: string) {
  await client.DELETE('/api/v1/sessions/{sessionId}/my-participation', {
    params: { header: csrf, path: { sessionId } },
  })
}

export async function requestJoin(sessionId: string) {
  const { data } = await client.POST(
    '/api/v1/sessions/{sessionId}/join-requests',
    { params: { header: csrf, path: { sessionId } } },
  )
  if (!data) throw new Error('Join request response was empty.')
  return data
}

export async function withdrawJoinRequest(sessionId: string) {
  await client.DELETE('/api/v1/sessions/{sessionId}/join-requests/mine', {
    params: { header: csrf, path: { sessionId } },
  })
}

export async function approveJoinRequest(
  sessionId: string,
  requestId: string,
) {
  const { data } = await client.POST(
    '/api/v1/sessions/{sessionId}/join-requests/{requestId}/approve',
    { params: { header: csrf, path: { sessionId, requestId } } },
  )
  if (!data) throw new Error('Approve response was empty.')
  return data
}

export async function rejectJoinRequest(
  sessionId: string,
  requestId: string,
) {
  await client.POST(
    '/api/v1/sessions/{sessionId}/join-requests/{requestId}/reject',
    { params: { header: csrf, path: { sessionId, requestId } } },
  )
}

export async function promoteWaitlisted(
  sessionId: string,
  participationId: string,
) {
  const { data } = await client.POST(
    '/api/v1/sessions/{sessionId}/participations/{participationId}/promote',
    { params: { header: csrf, path: { sessionId, participationId } } },
  )
  if (!data) throw new Error('Promotion response was empty.')
  return data
}

export async function rejectWaitlisted(
  sessionId: string,
  participationId: string,
) {
  await client.POST(
    '/api/v1/sessions/{sessionId}/participations/{participationId}/reject',
    { params: { header: csrf, path: { sessionId, participationId } } },
  )
}

export async function setReminder(
  sessionId: string,
  choice: ReminderChoiceValue,
) {
  const { data } = await client.PUT(
    '/api/v1/sessions/{sessionId}/my-reminder',
    {
      params: { header: csrf, path: { sessionId } },
      body: { choice },
    },
  )
  if (!data) throw new Error('Reminder response was empty.')
  return data
}

type InvalidateOptions = {
  removeDetail?: boolean
  includeJoinRequests?: boolean
}

/**
 * Every mutation invalidates its detail, the affected list family (both
 * scopes, since a materialized-status change can move a session into or
 * out of either), and the join-request key (design §10.1).
 */
export async function invalidateSessionQueries(
  queryClient: QueryClient,
  sessionId: string,
  { removeDetail = false, includeJoinRequests = true }: InvalidateOptions = {},
) {
  if (removeDetail) {
    queryClient.removeQueries({
      queryKey: sessionKeys.detail(sessionId),
      exact: true,
    })
  }

  const invalidations = [
    queryClient.invalidateQueries({ queryKey: ['sessions'] }),
  ]

  if (!removeDetail) {
    invalidations.push(
      queryClient.invalidateQueries({
        queryKey: sessionKeys.detail(sessionId),
        exact: true,
      }),
    )
  }

  if (includeJoinRequests) {
    invalidations.push(
      queryClient.invalidateQueries({
        queryKey: sessionKeys.joinRequests(sessionId),
        exact: true,
      }),
    )
  }

  await Promise.all(invalidations)
}

/**
 * Warns the browser before an unsaved close/reload while a create/edit
 * form is dirty (design §10.1 "close" discard duty).
 */
export function useDirtyGuard(dirty: boolean) {
  useEffect(() => {
    if (!dirty) return
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault()
    }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])
}

export function isSessionInProgress(session: {
  startTime: string
  durationMinutes: number | string
}) {
  const start = new Date(session.startTime).getTime()
  const end = start + Number(session.durationMinutes) * 60_000
  const now = Date.now()
  return now >= start && now < end
}
