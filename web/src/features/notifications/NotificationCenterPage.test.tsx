import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: () => false,
}))

import '../../i18n'
import { NotificationCenterPage } from './NotificationCenterPage'
import { notificationKeys } from './notificationQueries'

function notification(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'notif-1',
    type: 'sessionInvitation',
    createdAt: '2099-01-01T00:00:00Z',
    readAt: null,
    unread: true,
    ...overrides,
  }
}

// The page now fetches both the scoped list (`GET /api/v1/notifications`)
// and the unread-count query (F2) on every render, so the mock must answer
// each endpoint distinctly rather than one blanket `mockResolvedValue` —
// otherwise the count query reads the list payload's shape and silently
// resolves to `NaN`.
function mockNotifications({
  items,
  nextCursor = null,
  unreadCount,
}: {
  items: Array<Record<string, unknown>>
  nextCursor?: string | null
  unreadCount?: number
}) {
  const count =
    unreadCount ?? items.filter((item) => item.unread === true).length
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/notifications/unread-count') {
      return Promise.resolve({ data: { count } })
    }
    return Promise.resolve({ data: { items, nextCursor } })
  })
}

function renderPage(
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } }),
) {
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <NotificationCenterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { queryClient, ...utils }
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('NotificationCenterPage — ruling 1 (opening marks nothing)', () => {
  it('never calls the mark-read or mark-all-read endpoints on mount', async () => {
    mockNotifications({ items: [notification()] })

    renderPage()

    await screen.findByText('Session invite')
    // A microtask flush window for any accidental effect-driven mutation.
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(api.post).not.toHaveBeenCalled()
  })
})

describe('NotificationCenterPage — unread semantics (PHASE5_1_DESIGN.md §3 :53, never color-alone)', () => {
  it('marks an unread row with a dot glyph, bold title and an accessible name stating "unread"', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'unread-1',
          type: 'pollClosed',
          subjectId: 'poll-1',
          subjectKind: 'poll',
          subjectTitle: 'Raid night',
          unread: true,
        }),
      ],
    })

    renderPage()

    const link = await screen.findByRole('link', { name: /unread poll closed/i })
    expect(link.className).toMatch(/underline-offset/)
    expect(within(link).getByText('Poll closed').className).toMatch(/font-bold/)
  })

  it('does not mark a read row with the dot glyph or bold weight', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'read-1',
          type: 'pollClosed',
          subjectId: 'poll-1',
          subjectKind: 'poll',
          subjectTitle: 'Raid night',
          unread: false,
          readAt: '2099-01-02T00:00:00Z',
        }),
      ],
    })

    renderPage()

    const link = await screen.findByRole('link', { name: 'Poll closed' })
    expect(within(link).getByText('Poll closed').className).not.toMatch(/font-bold/)
  })
})

describe('NotificationCenterPage — masked-actor wire shapes (PHASE7_DESIGN.md §4)', () => {
  it('renders the named actor when both actor fields are present_D060', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'friend-named',
          type: 'friendRequestReceived',
          actorUserId: 'user-1',
          actorDisplayName: 'Jamie',
          unread: false,
        }),
      ],
    })

    renderPage()

    expect(await screen.findByText('Jamie')).toBeInTheDocument()
  })

  it('renders Anonymous User when actorUserId is present but actorDisplayName is absent (tombstoned/profile-incomplete)_D076', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'friend-tombstoned',
          type: 'friendRequestReceived',
          actorUserId: 'user-2',
          unread: false,
        }),
      ],
    })

    renderPage()

    expect(await screen.findByText('Anonymous User')).toBeInTheDocument()
  })

  it('renders Anonymous User when both actor fields are absent (blocked pair)_D076_D086', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'friend-blocked',
          type: 'steamImportMatch',
          unread: false,
        }),
      ],
    })

    renderPage()

    expect(await screen.findByText('Anonymous User')).toBeInTheDocument()
  })
})

describe('NotificationCenterPage — §2 minimal-form row renders unlinked', () => {
  it('renders no anchor and no navigating control for a subject-less row_D060', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'minimal-1',
          type: 'sessionCanceled',
          unread: true,
        }),
      ],
    })

    renderPage()

    await screen.findByText('Session canceled')
    expect(screen.queryByRole('link', { name: /session canceled/i })).not.toBeInTheDocument()
  })
})

describe('NotificationCenterPage — option start time (PHASE7_DESIGN.md §2, recipient-locale instant)', () => {
  const fixedInstant = '2099-03-04T18:30:00Z'
  const expectedFormatted = new Intl.DateTimeFormat('en', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(fixedInstant))

  it('renders optionStartTime formatted in the recipient locale for a pollOptionSuggested row_D060', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'opt-suggested',
          type: 'pollOptionSuggested',
          subjectId: 'poll-1',
          subjectKind: 'poll',
          subjectTitle: 'Raid night',
          optionStartTime: fixedInstant,
          unread: false,
        }),
      ],
    })

    renderPage()

    await screen.findByText('New time suggested')
    expect(screen.getByTestId('option-start-time')).toHaveTextContent(expectedFormatted)
  })

  it('renders optionStartTime formatted in the recipient locale for a pollOptionSelected row_D060', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'opt-selected',
          type: 'pollOptionSelected',
          subjectId: 'poll-1',
          subjectKind: 'poll',
          subjectTitle: 'Raid night',
          optionStartTime: fixedInstant,
          unread: false,
        }),
      ],
    })

    renderPage()

    await screen.findByText('Time selected')
    expect(screen.getByTestId('option-start-time')).toHaveTextContent(expectedFormatted)
  })

  it('renders no option-time placeholder when optionStartTime is absent', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'opt-absent',
          type: 'pollOptionSuggested',
          subjectId: 'poll-1',
          subjectKind: 'poll',
          subjectTitle: 'Raid night',
          optionStartTime: null,
          unread: false,
        }),
      ],
    })

    renderPage()

    await screen.findByText('New time suggested')
    expect(screen.queryByTestId('option-start-time')).not.toBeInTheDocument()
  })
})

describe('NotificationCenterPage — D-105 emitted-type inventory + unknown-type safety', () => {
  it('renders both emitted friend types (request-received, Steam-match) and no others_D105', async () => {
    mockNotifications({
      items: [
        notification({ id: 'f1', type: 'friendRequestReceived', actorDisplayName: 'Alex', unread: false }),
        notification({ id: 'f2', type: 'steamImportMatch', actorDisplayName: 'Sam', unread: false }),
      ],
    })

    renderPage()

    expect(await screen.findByText('New friend request')).toBeInTheDocument()
    expect(await screen.findByText('Friend found on Steam')).toBeInTheDocument()
    // The absence of acceptance/removal/blocking copy is proven by the closed
    // 25-type + unknown-fallback inventory (`i18n.keys.test.ts`'s "has exactly
    // one title per NotificationType wire value plus the unknown fallback") —
    // that test enumerates every wire type the backend ever emits, so no type
    // for acceptance/removal/blocking exists to feed through this renderer.
  })

  it('degrades an unrecognized type safely instead of erroring_D105', async () => {
    mockNotifications({
      items: [notification({ id: 'future-type', type: 'someFutureType', unread: false })],
    })

    renderPage()

    expect(await screen.findByText('New notification')).toBeInTheDocument()
  })
})

describe('NotificationCenterPage — mark read / mark all read', () => {
  it('marks a single row read and invalidates the notifications and unread-count queries', async () => {
    mockNotifications({ items: [notification({ id: 'n1', unread: true })] })
    api.post.mockResolvedValue({ data: undefined })

    const { queryClient } = renderPage()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    const button = await screen.findByRole('button', { name: /mark read/i })
    fireEvent.click(button)

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/notifications/{notificationId}/read',
        expect.objectContaining({ params: expect.objectContaining({ path: { notificationId: 'n1' } }) }),
      ),
    )

    // Asserts the invalidation itself happens — a query-key typo in
    // `invalidateNotifications` would leave `api.post` green while the
    // badge never drops (the finding this test closes).
    await waitFor(() =>
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['notifications'] }),
      ),
    )
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: notificationKeys.unreadCount }),
    )
  })

  it('marks all read via the header control and invalidates the notifications and unread-count queries', async () => {
    mockNotifications({ items: [notification({ id: 'n1', unread: true })] })
    api.post.mockResolvedValue({ data: undefined })

    const { queryClient } = renderPage()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    await screen.findByText('Session invite')
    const button = await screen.findByRole('button', { name: 'Mark all read' })
    await waitFor(() => expect(button).not.toBeDisabled())
    fireEvent.click(button)

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/notifications/read-all',
        expect.anything(),
      ),
    )

    await waitFor(() =>
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['notifications'] }),
      ),
    )
    expect(invalidateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ queryKey: notificationKeys.unreadCount }),
    )
  })

  it('disables Mark all read when nothing is unread', async () => {
    mockNotifications({
      items: [notification({ id: 'n1', unread: false, readAt: '2099-01-01T00:00:00Z' })],
    })

    renderPage()

    await screen.findByText('Session invite')
    const button = await screen.findByRole('button', { name: 'Mark all read' })
    expect(button).toBeDisabled()
  })

  it('enables Mark all read from the unread-count query even when the loaded page has zero unread items', async () => {
    // F2 pin: unread rows can sit entirely beyond page 1 — the loaded page
    // shows a fully-read item, but the badge-backing count query says 5.
    mockNotifications({
      items: [notification({ id: 'n1', unread: false, readAt: '2099-01-01T00:00:00Z' })],
      unreadCount: 5,
    })

    renderPage()

    await screen.findByText('Session invite')
    const button = await screen.findByRole('button', { name: 'Mark all read' })
    await waitFor(() => expect(button).not.toBeDisabled())
  })
})

describe('NotificationCenterPage — empty states and scope toggle', () => {
  it('renders the empty-all copy when the list has no items', async () => {
    mockNotifications({ items: [] })

    renderPage()

    expect(await screen.findByText('No notifications yet.')).toBeInTheDocument()
  })

  it('switches to the unread scope and refetches under the unread query param', async () => {
    mockNotifications({ items: [] })

    renderPage()

    const unreadTab = await screen.findByRole('tab', { name: 'Unread' })
    fireEvent.click(unreadTab)

    await waitFor(() =>
      expect(api.get).toHaveBeenCalledWith(
        '/api/v1/notifications',
        expect.objectContaining({
          params: expect.objectContaining({ query: expect.objectContaining({ scope: 'unread' }) }),
        }),
      ),
    )
  })

  it('a friend-graph row carries the actor disc and the link name excludes it_D188', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'n-friend',
          type: 'friendRequestReceived',
          actorUserId: 'user-3',
          actorDisplayName: 'Ada',
          actorAvatar: { kind: 'emoji', emoji: '🦉', avatarSourceId: null, imgUrl: null },
        }),
      ],
    })
    renderPage()

    const row = await screen.findByTestId('notification-n-friend')
    expect(row.querySelector('.notification-actor-disc')).toHaveTextContent('🦉')

    // The disc must never enter a control's accessible name. This is the
    // hazard the Tier 1 sweep found: getByLabelText saw the disc, but
    // computeAccessibleName did not. The name is the sr-only unread prefix
    // plus the type label (`NotificationCenterPage.tsx:104-107`), which for
    // `friendRequestReceived` is en's `item.types.friendRequestReceived`.
    const link = within(row).getByRole('link')
    expect(link).toHaveAccessibleName('Unread New friend request')
    expect(link).not.toHaveAccessibleName(/🦉/)
  })

  it('a session row shows its subject title and no person disc_D188', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'n-session',
          type: 'sessionInvitation',
          subjectKind: 'session',
          subjectId: '11111111-1111-1111-1111-111111111111',
          subjectTitle: 'Raid night',
          actorUserId: 'user-3',
          actorAvatar: { kind: 'emoji', emoji: '🔥', avatarSourceId: null, imgUrl: null },
        }),
      ],
    })
    renderPage()

    const row = await screen.findByTestId('notification-n-session')
    expect(row).toHaveTextContent('Raid night')
    // No person is named on this row, so nothing is decorated.
    expect(row.querySelector('.notification-actor-disc')).toBeNull()
  })

  it('a masked friend-graph row renders the neutral disc_D188_D076', async () => {
    mockNotifications({
      items: [
        notification({
          id: 'n-masked',
          type: 'friendRequestReceived',
          actorUserId: 'user-blocked',
          actorDisplayName: null,
          actorAvatar: { kind: 'emoji', emoji: '🌵', avatarSourceId: null, imgUrl: null },
        }),
      ],
    })
    renderPage()

    const row = await screen.findByTestId('notification-n-masked')
    const disc = row.querySelector('.notification-actor-disc')
    expect(disc).toHaveTextContent('●')
    expect(disc).not.toHaveTextContent('🌵')
  })
})
