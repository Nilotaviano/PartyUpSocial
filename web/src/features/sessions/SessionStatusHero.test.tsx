import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
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

function rosterEntry(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    participationId: 'p-1',
    userId: 'user-2',
    guestIdentityId: null,
    displayName: 'Ada',
    status: 'going',
    isWaitlisted: false,
    origin: 'invited',
    reminderChoice: null,
    ...overrides,
  }
}

function detail(overrides: Partial<Record<string, unknown>> = {}) {
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
    roster: [rosterEntry()],
    version: 'v1',
    muted: false,
    ...overrides,
  }
}

function mockDetail(data: unknown) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/sessions/{sessionId}') return Promise.resolve({ data })
    if (path === '/api/v1/sessions/{sessionId}/join-requests')
      return Promise.resolve({ data: { items: [], nextCursor: null } })
    if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
    throw new Error(`Unexpected GET ${path}`)
  })
}

function renderPage() {
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
  mockDetail(detail())
  api.put.mockResolvedValue({ data: {} })
  api.post.mockResolvedValue({ data: {} })
  api.delete.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('session detail status hero', () => {
  it.each([
    ['unconfirmed', 'Waiting for players'],
    ['confirmed', 'Confirmed'],
    ['completed', 'Completed'],
    ['expired', 'Expired'],
    ['canceled', 'Canceled'],
  ])('names the %s status as text, never colour alone_D139', async (status, label) => {
    mockDetail(detail({ status }))
    renderPage()

    expect(await screen.findByTestId('session-status-chip')).toHaveTextContent(label)
  })

  it('renders no chip for a status this build does not know', async () => {
    mockDetail(detail({ status: 'someFutureStatus' }))
    renderPage()

    expect(await screen.findByText('Raid night')).toBeInTheDocument()
    expect(screen.queryByTestId('session-status-chip')).not.toBeInTheDocument()
  })

  it('adds the in-progress chip alongside the status chip_D095', async () => {
    const now = Date.now()
    mockDetail(
      detail({
        startTime: new Date(now - 60_000).toISOString(),
        durationMinutes: 60,
        status: 'confirmed',
      }),
    )
    renderPage()

    expect(await screen.findByTestId('session-status-chip')).toHaveTextContent('Confirmed')
    expect(screen.getByText('In progress')).toBeInTheDocument()
  })

  it('shows the progress line only while unconfirmed_D074', async () => {
    mockDetail(detail({ status: 'unconfirmed' }))
    const { unmount } = renderPage()
    expect(await screen.findByText(/1 of 4 confirmed/)).toBeInTheDocument()
    unmount()
    cleanup()

    mockDetail(detail({ status: 'confirmed' }))
    renderPage()
    expect(await screen.findByTestId('session-status-chip')).toHaveTextContent('Confirmed')
    expect(screen.queryByText(/of 4 confirmed/)).not.toBeInTheDocument()
  })

  it('counts only non-waitlisted Going rows in the progress numerator_D062', async () => {
    mockDetail(
      detail({
        roster: [
          rosterEntry({ participationId: 'p-a' }),
          rosterEntry({ participationId: 'p-b', displayName: 'Bo' }),
          rosterEntry({ participationId: 'p-c', displayName: 'Cy', isWaitlisted: true }),
          rosterEntry({ participationId: 'p-d', displayName: 'Di', status: 'maybe' }),
        ],
      }),
    )
    renderPage()

    expect(await screen.findByText(/2 of 4 confirmed/)).toBeInTheDocument()
  })

  it('adds the waitlist sentence only when the session has a maximum capacity', async () => {
    mockDetail(detail({ maxCapacity: 8 }))
    const { unmount } = renderPage()
    expect(await screen.findByText(/join the waitlist/)).toBeInTheDocument()
    unmount()
    cleanup()

    mockDetail(detail({ maxCapacity: null }))
    renderPage()
    expect(await screen.findByText(/1 of 4 confirmed/)).toBeInTheDocument()
    expect(screen.queryByText(/join the waitlist/)).not.toBeInTheDocument()
  })
})

describe('session detail RSVP group', () => {
  it('never mutates a response while the keyboard traverses the group_D084', async () => {
    renderPage()

    const going = await screen.findByRole('button', { name: 'Going' })
    const maybe = screen.getByRole('button', { name: 'Maybe' })
    const declined = screen.getByRole('button', { name: 'Declined' })

    for (const button of [going, maybe, declined]) {
      button.focus()
      expect(button).toHaveFocus()
      fireEvent.keyDown(button, { key: 'ArrowRight' })
      fireEvent.keyDown(button, { key: 'ArrowLeft' })
      fireEvent.keyDown(button, { key: 'ArrowDown' })
      fireEvent.keyDown(button, { key: 'Tab' })
      fireEvent.keyUp(button, { key: 'Tab' })
    }

    expect(api.put).not.toHaveBeenCalled()

    // The same group still commits on explicit activation, so the assertion
    // above is about traversal and not about a dead control.
    fireEvent.click(going)
    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/my-participation',
        expect.objectContaining({ body: { response: 'Going' } }),
      ),
    )
  })

  it('keeps each RSVP choice a pressed-button, not a radio_D084', async () => {
    renderPage()

    const going = await screen.findByRole('button', { name: 'Going' })
    expect(going).toHaveAttribute('aria-pressed')
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument()
    expect(screen.queryAllByRole('radio')).toHaveLength(0)
  })
})
