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
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
  ApiError: class ApiError extends Error {
    status: number
    code: string
    constructor(status: number, code: string) {
      super(code)
      this.status = status
      this.code = code
    }
  },
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
    notes: 'Bring snacks',
    minAttendance: 4,
    maxCapacity: 8,
    isPrivate: false,
    status: 'unconfirmed',
    createdAt: '2030-08-01T00:00:00Z',
    roster: [
      {
        participationId: 'p-1',
        userId: 'user-2',
        guestIdentityId: null,
        displayName: null,
        status: 'going',
        isWaitlisted: false,
        origin: 'invited',
        reminderChoice: null,
      },
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

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/sessions/session-1']}>
        <Routes>
          <Route
            path="/sessions/upcoming"
            element={<div data-testid="screen-sessions-upcoming" />}
          />
          <Route path="/sessions/:sessionId" element={<SessionDetailPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { ...utils, queryClient }
}

beforeEach(() => {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/sessions/{sessionId}')
      return Promise.resolve({ data: detail() })
    if (path === '/api/v1/sessions/{sessionId}/join-requests')
      return Promise.resolve({ data: { items: [], nextCursor: null } })
    if (path === '/api/v1/me')
      return Promise.resolve({ data: { timeZone: null } })
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

describe('SessionDetailPage', () => {
  it('masks a participant with no display name as Anonymous User_D076_D114', async () => {
    renderPage()

    expect(await screen.findByText('Anonymous User')).toBeInTheDocument()
    expect(screen.getByText('Guest Bob')).toBeInTheDocument()
  })

  it('RSVP responses invalidate the session detail and both list families_D006', async () => {
    const { queryClient } = renderPage()
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')

    fireEvent.click(await screen.findByRole('button', { name: 'Going' }))

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/my-participation',
        expect.objectContaining({ body: { response: 'Going' } }),
      ),
    )
    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['sessions'] }),
      )
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['session', 'session-1'] }),
      )
      expect(invalidateSpy).toHaveBeenCalledWith(
        expect.objectContaining({ queryKey: ['session', 'join-requests', 'session-1'] }),
      )
    })
  })

  it('sends a join request', async () => {
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Request to join' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/join-requests',
        expect.anything(),
      ),
    )
  })

  it('promotes a waitlisted participant_D084', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        const data = detail()
        data.roster[0].isWaitlisted = true
        return Promise.resolve({ data })
      }
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Promote' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/participations/{participationId}/promote',
        expect.objectContaining({
          params: expect.objectContaining({
            path: { sessionId: 'session-1', participationId: 'p-1' },
          }),
        }),
      ),
    )
  })

  it('requires organizer confirmation before removing a guest_D044', async () => {
    renderPage()

    const trigger = await screen.findByRole('button', { name: 'Remove guest Guest Bob' })
    fireEvent.click(trigger)

    const dialog = screen.getByRole('dialog')
    expect(
      within(dialog).getByText(/lose access immediately/i),
    ).toBeInTheDocument()
    expect(api.delete).not.toHaveBeenCalled()

    fireEvent.click(within(dialog).getAllByRole('button', { name: 'Remove guest' })[0])

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/participations/{participationId}/guest',
        expect.objectContaining({
          params: expect.objectContaining({
            path: { sessionId: 'session-1', participationId: 'p-guest' },
          }),
        }),
      ),
    )
  })

  it('cancel confirmation removes the detail and returns to the upcoming list', async () => {
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Cancel session' }))
    const dialog = screen.getByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel session' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}',
        expect.anything(),
      ),
    )
    await waitFor(() =>
      expect(screen.getByTestId('screen-sessions-upcoming')).toBeInTheDocument(),
    )
  })

  it('binds the reminder select to the viewer roster entry_H3', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        const data = detail()
        data.roster[0].userId = 'organizer-1'
        ;(data.roster[0] as { reminderChoice: string | null }).reminderChoice = 'Hours1'
        return Promise.resolve({ data })
      }
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    const select = (await screen.findByLabelText('Reminder')) as HTMLSelectElement
    await waitFor(() => expect(select.value).toBe('Hours1'))
  })

  it('renders the session start time in the viewer profile time zone rather than the browser zone_D038_D046', async () => {
    const format = (timeZone?: string) =>
      new Intl.DateTimeFormat('en', {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone,
      }).format(new Date('2030-09-01T18:00:00Z'))
    const hostRender = format()
    // A profile zone whose rendering provably differs from the host zone, so the
    // assertion fails if the component silently falls back to the browser zone.
    const profileZone = ['Asia/Tokyo', 'Pacific/Honolulu'].find(
      (zone) => format(zone) !== hostRender,
    )!
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}')
        return Promise.resolve({ data: detail() })
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me')
        return Promise.resolve({ data: { timeZone: profileZone } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText(format(profileZone))).toBeInTheDocument()
  })
})
