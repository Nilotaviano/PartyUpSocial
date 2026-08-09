import { describe, expect, it } from 'vitest'
import { isMinimalNotification, notificationHref } from './notificationQueries'
import type { NotificationItem } from './notificationQueries'

function notification(overrides: Partial<NotificationItem> = {}): NotificationItem {
  return {
    id: 'notif-1',
    type: 'sessionInvitation',
    createdAt: '2099-01-01T00:00:00Z',
    readAt: null,
    actorUserId: null,
    actorDisplayName: null,
    subjectKind: null,
    subjectId: null,
    subjectTitle: null,
    optionStartTime: null,
    unread: true,
    ...overrides,
  }
}

describe('notificationHref (PHASE7_DESIGN.md §4/§5)', () => {
  it('routes friend-graph types to /friends by type, never by subjectKind_D105', () => {
    expect(notificationHref(notification({ type: 'friendRequestReceived' }))).toBe('/friends')
    expect(notificationHref(notification({ type: 'steamImportMatch' }))).toBe('/friends')
  })

  it('routes a session-kind row to /sessions/{id}', () => {
    expect(
      notificationHref(
        notification({ type: 'sessionConfirmed', subjectKind: 'session', subjectId: 'sess-1' }),
      ),
    ).toBe('/sessions/sess-1')
  })

  it('routes a poll-kind row to /polls/{id}', () => {
    expect(
      notificationHref(
        notification({ type: 'pollClosed', subjectKind: 'poll', subjectId: 'poll-1' }),
      ),
    ).toBe('/polls/poll-1')
  })

  it('returns null for a §2 minimal-form row (no subjectKind/subjectId)_D076', () => {
    expect(notificationHref(notification({ type: 'sessionCanceled' }))).toBeNull()
    expect(isMinimalNotification(notification({ type: 'sessionCanceled' }))).toBe(true)
  })
})
