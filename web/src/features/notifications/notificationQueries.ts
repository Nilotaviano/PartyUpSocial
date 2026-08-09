import type { QueryClient } from '@tanstack/react-query'
import type { components } from '../../api/schema'
import { client } from '../../api/client'

export type NotificationItem = components['schemas']['NotificationResponse']
export type NotificationScope = 'all' | 'unread'

// PHASE7_DESIGN.md §4: the two wire scopes carry distinct A-072 cursors — a
// cursor issued under one scope is `400 invalid_cursor` under the other, so
// each scope gets its own query-key branch (never shared with the other).
export const notificationKeys = {
  list: (scope: NotificationScope, cursor?: string) =>
    ['notifications', scope, cursor ?? null] as const,
  unreadCount: ['notifications', 'unread-count'] as const,
}

export async function getNotificationList(scope: NotificationScope, cursor?: string) {
  const { data } = await client.GET('/api/v1/notifications', {
    params: { query: { scope, cursor } },
  })
  if (!data) throw new Error('Notification list response was empty.')
  return data
}

export async function getUnreadNotificationCount() {
  const { data } = await client.GET('/api/v1/notifications/unread-count')
  if (!data) throw new Error('Unread notification count response was empty.')
  return Number(data.count)
}

const csrf = { 'X-CSRF-TOKEN': '' }

export async function markNotificationRead(notificationId: string) {
  await client.POST('/api/v1/notifications/{notificationId}/read', {
    params: { header: csrf, path: { notificationId } },
  })
}

export async function markAllNotificationsRead() {
  await client.POST('/api/v1/notifications/read-all', {
    params: { header: csrf },
  })
}

// Design §5: mark-read mutations invalidate both `['notifications']` (every
// scoped list page, any cursor) and `['notifications','unread-count']` (the
// bell badge) — the same QueryClient instance backs the center page and the
// chrome bell, so one call refreshes both surfaces.
export async function invalidateNotifications(queryClient: QueryClient) {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: ['notifications'] }),
    queryClient.invalidateQueries({ queryKey: notificationKeys.unreadCount }),
  ])
}

const FRIEND_TYPES = new Set(['friendRequestReceived', 'steamImportMatch'])

/**
 * Design §5/§4: friend-graph rows (D-105) never carry a session/poll ref by
 * shape (`NotificationTypeExtensions.NeedsActorRef`), so they route to
 * `/friends` by `type` alone, never by `subjectKind` (the wire never emits
 * `subjectKind: "friend"`). A session/poll row with no reachable subject
 * (§2's masked minimal form) carries neither `subjectKind` nor `subjectId`
 * and is therefore unlinked.
 */
export function notificationHref(notification: NotificationItem): string | null {
  if (FRIEND_TYPES.has(notification.type)) return '/friends'
  if (notification.subjectKind === 'session' && notification.subjectId) {
    return `/sessions/${notification.subjectId}`
  }
  if (notification.subjectKind === 'poll' && notification.subjectId) {
    return `/polls/${notification.subjectId}`
  }
  return null
}

export function isMinimalNotification(notification: NotificationItem): boolean {
  return notificationHref(notification) === null
}
