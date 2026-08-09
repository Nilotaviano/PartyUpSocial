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

const mockedUseSession = vi.hoisted(() => vi.fn())

vi.mock('../../auth/useSession', () => ({
  useSession: mockedUseSession,
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
    muted: false,
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
  mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
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

  it('mutes an unmuted session via PUT, shows the D-090 suppression copy, and announces the muted state_D090', async () => {
    renderPage()

    const toggle = await screen.findByRole('button', { name: 'Mute' })
    expect(screen.getByText(/does not backfill/i)).toBeInTheDocument()
    fireEvent.click(toggle)

    await waitFor(() =>
      expect(api.put).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/mute',
        expect.objectContaining({
          params: expect.objectContaining({ path: { sessionId: 'session-1' } }),
        }),
      ),
    )
    expect(await screen.findByText('Session muted.')).toBeInTheDocument()
  })

  it('unmutes a muted session via DELETE and announces the unmuted state (not the muted copy)_D090', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        const data = detail()
        data.muted = true
        return Promise.resolve({ data })
      }
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Unmute' }))

    await waitFor(() =>
      expect(api.delete).toHaveBeenCalledWith(
        '/api/v1/sessions/{sessionId}/mute',
        expect.objectContaining({
          params: expect.objectContaining({ path: { sessionId: 'session-1' } }),
        }),
      ),
    )
    expect(await screen.findByText('Session unmuted.')).toBeInTheDocument()
    expect(screen.queryByText('Session muted.')).not.toBeInTheDocument()
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

  it('offers no join action to a removed viewer with inherited access_D018', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'viewer-1' } })
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        const data = detail()
        data.roster.push({
          participationId: 'p-removed',
          userId: 'viewer-1',
          guestIdentityId: null,
          displayName: 'Removed Person',
          status: 'removed',
          isWaitlisted: false,
          origin: 'invited',
          reminderChoice: null,
        } as unknown as (typeof data.roster)[number])
        return Promise.resolve({ data })
      }
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText('Raid night')).toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Request to join' }),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Withdraw request' }),
    ).not.toBeInTheDocument()
  })

  it('renders a roster entry with no user id as anonymous_D076', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: undefined } })
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}') {
        const data = detail()
        // A named row, unaffected — proves the masked row's Anonymous
        // rendering isn't just the fixture's own null-displayName row
        // coincidentally satisfying the assertion (round-1 F4).
        data.roster[0].displayName = 'Named Friend'
        // A blocked-pair-masked entry: `userId` is absent on the wire
        // (JsonIgnore WhenWritingNull), not merely null (§5.1). A unique
        // participationId lets the assertion scope to this exact row —
        // deleting it must fail the test, not just remove one match among
        // several.
        data.roster.push({
          participationId: 'p-masked-unique',
          guestIdentityId: null,
          displayName: null,
          status: 'going',
          isWaitlisted: false,
          origin: 'invited',
          reminderChoice: null,
        } as unknown as (typeof data.roster)[number])
        return Promise.resolve({ data })
      }
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    const maskedRow = await screen.findByTestId('roster-entry-p-masked-unique')
    expect(within(maskedRow).getByText('Anonymous User')).toBeInTheDocument()
    expect(screen.getByText('Named Friend')).toBeInTheDocument()
  })

  it('names the requester on an organizer join-request row instead of the raw id_D103', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}')
        return Promise.resolve({ data: detail() })
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({
          data: {
            items: [
              {
                id: 'jr-1',
                requesterId: '11111111-1111-1111-1111-111111111111',
                status: 'pending',
                createdAt: '2030-08-01T00:00:00Z',
                requester: { ordinal: 0, displayName: 'Ada Requester' },
              },
            ],
            nextCursor: null,
          },
        })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    expect(await screen.findByText('Ada Requester')).toBeInTheDocument()
    expect(
      screen.queryByText('11111111-1111-1111-1111-111111111111'),
    ).not.toBeInTheDocument()
  })
})
