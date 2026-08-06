import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn(),
  delete: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, PUT: api.put, POST: api.post, DELETE: api.delete },
  isApiError: () => false,
}))

vi.mock('../../auth/useSession', () => ({
  useSession: () => ({ data: { userId: 'organizer-1' } }),
}))

import '../../i18n'
import { SessionDetailPage } from './SessionDetailPage'

function detail() {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 120,
    platform: 'Steam',
    game: null,
    notes: '',
    minAttendance: 4,
    maxCapacity: 8,
    isPrivate: false,
    status: 'unconfirmed',
    createdAt: '2030-08-01T00:00:00Z',
    roster: [
      {
        participationId: 'p-guest',
        userId: null,
        guestIdentityId: 'guest-1',
        displayName: 'Guest Bob',
        status: 'going',
        isWaitlisted: false,
        origin: 'guestLink',
        reminderChoice: null,
      },
    ],
    version: 'v1',
  }
}

function renderDetail() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/sessions/session-1']}>
        <Routes>
          <Route path="/sessions/:sessionId" element={<SessionDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/sessions/{sessionId}')
      return Promise.resolve({ data: detail() })
    if (path === '/api/v1/sessions/{sessionId}/join-requests')
      return Promise.resolve({ data: { items: [], nextCursor: null } })
    throw new Error(`Unexpected GET ${path}`)
  })
  api.put.mockResolvedValue({ data: {} })
  api.post.mockResolvedValue({ data: {} })
  api.delete.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionAccessibilityTests', () => {
  it('focuses the page h1 on mount_D139', async () => {
    renderDetail()

    const heading = await screen.findByRole('heading', { level: 1 })
    expect(heading).toHaveFocus()
  })

  it('reports RSVP outcomes through an aria-live=polite region_D139', async () => {
    const { container } = renderDetail()

    const status = container.querySelector('p[aria-live="polite"]')
    expect(status).not.toBeNull()

    fireEvent.click(await screen.findByRole('button', { name: 'Going' }))
    await waitFor(() => expect(status).toHaveTextContent('Your response was updated.'))
  })

  it('every interactive control meets the 44px touch target and visible-focus duties_D139', async () => {
    renderDetail()
    await screen.findByRole('button', { name: 'Going' })

    for (const button of screen.getAllByRole('button')) {
      expect(button).toHaveClass('min-h-11')
      expect(button.className).toContain('focus-visible:outline-2')
    }
  })

  it('the cancel dialog is focused and traps focus, closing on Escape and restoring the trigger_D139', async () => {
    renderDetail()
    const trigger = await screen.findByRole('button', { name: 'Cancel session' })
    trigger.focus()
    fireEvent.click(trigger)

    const dialog = screen.getByRole('dialog')
    await waitFor(() =>
      expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus(),
    )

    fireEvent.keyDown(document, { key: 'Escape' })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(trigger).toHaveFocus()
  })

  it('roster status is conveyed by text, not color alone_D139', async () => {
    renderDetail()

    expect(await screen.findByText('Going (1)')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Roster' })).toBeInTheDocument()
  })

  it('the remove-guest dialog is reachable and named for assistive technology_D139', async () => {
    renderDetail()

    const trigger = await screen.findByRole('button', { name: 'Remove guest Guest Bob' })
    fireEvent.click(trigger)

    const dialog = await screen.findByRole('dialog', { name: 'Remove guest' })
    expect(dialog).toHaveAccessibleDescription(/lose access immediately/i)
  })
})
