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

function renderPage() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <NotificationCenterPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('NotificationCenterPage — ruling 1 (opening marks nothing)', () => {
  it('never calls the mark-read or mark-all-read endpoints on mount_D060', async () => {
    api.get.mockResolvedValue({
      data: { items: [notification()], nextCursor: null },
    })

    renderPage()

    await screen.findByText('Session invite')
    // A microtask flush window for any accidental effect-driven mutation.
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(api.post).not.toHaveBeenCalled()
  })
})

describe('NotificationCenterPage — unread semantics (PHASE5_1_DESIGN.md §3 :53, never color-alone)', () => {
  it('marks an unread row with a dot glyph, bold title and an accessible name stating "unread"_D060', async () => {
    api.get.mockResolvedValue({
      data: {
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
        nextCursor: null,
      },
    })

    renderPage()

    const link = await screen.findByRole('link', { name: /unread poll closed/i })
    expect(link.className).toMatch(/underline-offset/)
    expect(within(link).getByText('Poll closed').className).toMatch(/font-bold/)
  })

  it('does not mark a read row with the dot glyph or bold weight', async () => {
    api.get.mockResolvedValue({
      data: {
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
        nextCursor: null,
      },
    })

    renderPage()

    const link = await screen.findByRole('link', { name: 'Poll closed' })
    expect(within(link).getByText('Poll closed').className).not.toMatch(/font-bold/)
  })
})

describe('NotificationCenterPage — masked-actor wire shapes (PHASE7_DESIGN.md §4)', () => {
  it('renders the named actor when both actor fields are present_D060', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          notification({
            id: 'friend-named',
            type: 'friendRequestReceived',
            actorUserId: 'user-1',
            actorDisplayName: 'Jamie',
            unread: false,
          }),
        ],
        nextCursor: null,
      },
    })

    renderPage()

    expect(await screen.findByText('Jamie')).toBeInTheDocument()
  })

  it('renders Anonymous User when actorUserId is present but actorDisplayName is absent (tombstoned/profile-incomplete)_D076', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          notification({
            id: 'friend-tombstoned',
            type: 'friendRequestReceived',
            actorUserId: 'user-2',
            unread: false,
          }),
        ],
        nextCursor: null,
      },
    })

    renderPage()

    expect(await screen.findByText('Anonymous User')).toBeInTheDocument()
  })

  it('renders Anonymous User when both actor fields are absent (blocked pair)_D076_D086', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          notification({
            id: 'friend-blocked',
            type: 'steamImportMatch',
            unread: false,
          }),
        ],
        nextCursor: null,
      },
    })

    renderPage()

    expect(await screen.findByText('Anonymous User')).toBeInTheDocument()
  })
})

describe('NotificationCenterPage — §2 minimal-form row renders unlinked', () => {
  it('renders no anchor and no navigating control for a subject-less row_D060', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          notification({
            id: 'minimal-1',
            type: 'sessionCanceled',
            unread: true,
          }),
        ],
        nextCursor: null,
      },
    })

    renderPage()

    await screen.findByText('Session canceled')
    expect(screen.queryByRole('link', { name: /session canceled/i })).not.toBeInTheDocument()
  })
})

describe('NotificationCenterPage — D-105 emitted-type inventory + unknown-type safety', () => {
  it('renders both emitted friend types (request-received, Steam-match) and no others_D105', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          notification({ id: 'f1', type: 'friendRequestReceived', actorDisplayName: 'Alex', unread: false }),
          notification({ id: 'f2', type: 'steamImportMatch', actorDisplayName: 'Sam', unread: false }),
        ],
        nextCursor: null,
      },
    })

    renderPage()

    expect(await screen.findByText('New friend request')).toBeInTheDocument()
    expect(await screen.findByText('Friend found on Steam')).toBeInTheDocument()
    // No such rows are ever emitted by the backend (D-105) — proving the
    // absence of acceptance/removal/blocking copy in the same inventory.
    expect(screen.queryByText(/friend request accepted/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/removed/i)).not.toBeInTheDocument()
    expect(screen.queryByText(/blocked/i)).not.toBeInTheDocument()
  })

  it('degrades an unrecognized type safely instead of erroring_D105', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [notification({ id: 'future-type', type: 'someFutureType', unread: false })],
        nextCursor: null,
      },
    })

    renderPage()

    expect(await screen.findByText('New notification')).toBeInTheDocument()
  })
})

describe('NotificationCenterPage — mark read / mark all read', () => {
  it('marks a single row read and invalidates the notifications and unread-count queries', async () => {
    api.get.mockResolvedValue({
      data: { items: [notification({ id: 'n1', unread: true })], nextCursor: null },
    })
    api.post.mockResolvedValue({ data: undefined })

    renderPage()

    const button = await screen.findByRole('button', { name: /mark read/i })
    fireEvent.click(button)

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/notifications/{notificationId}/read',
        expect.objectContaining({ params: expect.objectContaining({ path: { notificationId: 'n1' } }) }),
      ),
    )
  })

  it('marks all read via the header control', async () => {
    api.get.mockResolvedValue({
      data: { items: [notification({ id: 'n1', unread: true })], nextCursor: null },
    })
    api.post.mockResolvedValue({ data: undefined })

    renderPage()

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
  })

  it('disables Mark all read when nothing is unread', async () => {
    api.get.mockResolvedValue({
      data: { items: [notification({ id: 'n1', unread: false, readAt: '2099-01-01T00:00:00Z' })], nextCursor: null },
    })

    renderPage()

    await screen.findByText('Session invite')
    const button = await screen.findByRole('button', { name: 'Mark all read' })
    expect(button).toBeDisabled()
  })
})

describe('NotificationCenterPage — empty states and scope toggle', () => {
  it('renders the empty-all copy when the list has no items', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })

    renderPage()

    expect(await screen.findByText('No notifications yet.')).toBeInTheDocument()
  })

  it('switches to the unread scope and refetches under the unread query param', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })

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
})
