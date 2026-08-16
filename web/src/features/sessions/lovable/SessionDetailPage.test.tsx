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

vi.mock('../../../api/client', () => ({
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

vi.mock('../../../auth/useSession', () => ({
  useSession: mockedUseSession,
}))

import '../../../i18n'
import { SessionDetailPage } from './SessionDetailPage'

function detail(overrides: Partial<Record<string, unknown>> = {}) {
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
    ...overrides,
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

describe('SessionDetailPage (lovable)', () => {
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

  it('renders the session end time, closing the HIGH duration-never-rendered gap', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}')
        return Promise.resolve({ data: detail({ startTime: '2030-09-01T18:00:00Z', durationMinutes: 120 }) })
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: 'UTC' } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    const endText = new Intl.DateTimeFormat('en', { timeStyle: 'short', timeZone: 'UTC' }).format(
      new Date('2030-09-01T20:00:00Z'),
    )
    expect(await screen.findByText(`Ends ${endText}`)).toBeInTheDocument()
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

  it('renders an avatar disc on each roster row, decoration only_D188', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
    api.get.mockResolvedValue({
      data: detail({
        roster: [
          {
            participationId: 'p-named',
            userId: 'user-9',
            guestIdentityId: null,
            displayName: 'Ada',
            status: 'going',
            isWaitlisted: false,
            origin: 'invited',
            reminderChoice: null,
            avatar: { kind: 'emoji', emoji: '🦊', avatarSourceId: null, imgUrl: null },
          },
        ],
      }),
    })
    renderPage()

    const row = await screen.findByTestId('roster-entry-p-named')
    const disc = row.querySelector('[aria-hidden="true"]')
    expect(disc).toHaveTextContent('🦊')
    // Decoration: the row still reads as the name alone to a screen reader.
    expect(row).toHaveTextContent('Ada')
  })

  it('a masked roster row renders the neutral disc and leaks no emoji_D188_D076', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
    api.get.mockResolvedValue({
      data: detail({
        roster: [
          {
            participationId: 'p-masked',
            userId: 'user-blocked',
            guestIdentityId: null,
            displayName: null,
            // Even if the server ever emitted one, the render must not show it.
            avatar: { kind: 'emoji', emoji: '🐉', avatarSourceId: null, imgUrl: null },
            status: 'going',
            isWaitlisted: false,
            origin: 'invited',
            reminderChoice: null,
          },
        ],
      }),
    })
    renderPage()

    const row = await screen.findByTestId('roster-entry-p-masked')
    const disc = row.querySelector('[aria-hidden="true"]')
    expect(disc).toHaveTextContent('●')
    expect(disc).not.toHaveTextContent('🐉')
    expect(disc).toHaveClass('bg-busy')
  })

  it('the remove-guest control keeps its own accessible name, disc excluded_D188', async () => {
    mockedUseSession.mockReturnValue({ data: { userId: 'organizer-1' } })
    api.get.mockResolvedValue({
      data: detail({
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
            avatar: { kind: 'emoji', emoji: '🍕', avatarSourceId: null, imgUrl: null },
          },
        ],
      }),
    })
    renderPage()

    const row = await screen.findByTestId('roster-entry-p-guest')
    const remove = within(row).getByRole('button')
    expect(remove).toHaveAccessibleName('Remove guest Guest Bob')
    expect(remove).not.toHaveAccessibleName(/🍕/)
  })

  it('maps the notApplicable wire platform value to a localized "Other" label, not the raw wire value', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/{sessionId}')
        return Promise.resolve({ data: detail({ platform: 'notApplicable' }) })
      if (path === '/api/v1/sessions/{sessionId}/join-requests')
        return Promise.resolve({ data: { items: [], nextCursor: null } })
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
      throw new Error(`Unexpected GET ${path}`)
    })
    renderPage()

    await screen.findByText('Raid night')
    expect(screen.queryByText('notApplicable')).not.toBeInTheDocument()
    expect(screen.getByText('Other')).toBeInTheDocument()
  })

  describe('accessibility (D-139)', () => {
    it('focuses the page h1 on mount_D139', async () => {
      renderPage()

      const heading = await screen.findByRole('heading', { level: 1 })
      await waitFor(() => expect(heading).toHaveFocus())
    })

    it('reports RSVP outcomes through an aria-live=polite region_D139', async () => {
      const { container } = renderPage()

      const status = container.querySelector('p[aria-live="polite"]')
      expect(status).not.toBeNull()

      fireEvent.click(await screen.findByRole('button', { name: 'Going' }))
      await waitFor(() => expect(status).toHaveTextContent('Your response was updated.'))
    })

    it('every interactive control meets the 44px touch target and visible-focus duties_D139', async () => {
      renderPage()
      await screen.findByRole('button', { name: 'Going' })

      for (const button of screen.getAllByRole('button')) {
        expect(button).toHaveClass('min-h-11')
        expect(button.className).toContain('focus-visible:outline-2')
      }
    })

    it('the cancel dialog is focused and traps focus, closing on Escape and restoring the trigger_D139', async () => {
      renderPage()
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
      renderPage()

      expect(await screen.findByText('Going (2)')).toBeInTheDocument()
      expect(screen.getByRole('heading', { name: /Roster/ })).toBeInTheDocument()
    })

    it('exposes the roster as a named region, not a bare aria-labelled div_D139', async () => {
      renderPage()

      await screen.findByText('Going (2)')
      expect(screen.getByRole('region', { name: 'Roster' })).toBeInTheDocument()
    })

    it('the remove-guest dialog is reachable and named for assistive technology_D139', async () => {
      renderPage()

      const trigger = await screen.findByRole('button', { name: 'Remove guest Guest Bob' })
      fireEvent.click(trigger)

      const dialog = await screen.findByRole('dialog', { name: 'Remove guest' })
      expect(dialog).toHaveAccessibleDescription(/lose access immediately/i)
    })
  })

  describe('status hero (ported from the flat page SessionStatusHero.test.tsx)', () => {
    it.each([
      ['unconfirmed', 'Waiting for players'],
      ['confirmed', 'Confirmed'],
      ['completed', 'Completed'],
      ['expired', 'Expired'],
      ['canceled', 'Canceled'],
    ])('names the %s status as text, never colour alone_D139', async (status, label) => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({ data: detail({ status }) })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()

      const screenRoot = screen.getByTestId('screen-session-detail')
      expect(await within(screenRoot).findByText(label)).toBeInTheDocument()
    })

    it('renders no status pill for a status this build does not know', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({ data: detail({ status: 'someFutureStatus' }) })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()

      expect(await screen.findByText('Raid night')).toBeInTheDocument()
      for (const label of [
        'Waiting for players',
        'Confirmed',
        'Completed',
        'Expired',
        'Canceled',
      ]) {
        expect(screen.queryByText(label)).not.toBeInTheDocument()
      }
    })

    it('adds the in-progress chip alongside the status pill_D095', async () => {
      const now = Date.now()
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({
            data: detail({
              startTime: new Date(now - 60_000).toISOString(),
              durationMinutes: 60,
              status: 'confirmed',
            }),
          })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()

      expect(await screen.findByText('Confirmed')).toBeInTheDocument()
      expect(screen.getByText('In progress')).toBeInTheDocument()
    })

    it('shows the progress line only while unconfirmed_D074', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({ data: detail({ status: 'unconfirmed' }) })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      const { unmount } = renderPage()
      expect(await screen.findByText(/2 of 4 confirmed/)).toBeInTheDocument()
      unmount()
      cleanup()

      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({ data: detail({ status: 'confirmed' }) })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()
      expect(await screen.findByText('Confirmed')).toBeInTheDocument()
      expect(screen.queryByText(/of 4 confirmed/)).not.toBeInTheDocument()
    })

    it('counts only non-waitlisted Going rows in the progress numerator_D062', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({
            data: detail({
              roster: [
                {
                  participationId: 'p-a',
                  userId: 'user-a',
                  guestIdentityId: null,
                  displayName: 'Ada',
                  status: 'going',
                  isWaitlisted: false,
                  origin: 'invited',
                  reminderChoice: null,
                },
                {
                  participationId: 'p-b',
                  userId: 'user-b',
                  guestIdentityId: null,
                  displayName: 'Bo',
                  status: 'going',
                  isWaitlisted: false,
                  origin: 'invited',
                  reminderChoice: null,
                },
                {
                  participationId: 'p-c',
                  userId: 'user-c',
                  guestIdentityId: null,
                  displayName: 'Cy',
                  status: 'going',
                  isWaitlisted: true,
                  origin: 'invited',
                  reminderChoice: null,
                },
                {
                  participationId: 'p-d',
                  userId: 'user-d',
                  guestIdentityId: null,
                  displayName: 'Di',
                  status: 'maybe',
                  isWaitlisted: false,
                  origin: 'invited',
                  reminderChoice: null,
                },
              ],
            }),
          })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()

      expect(await screen.findByText(/2 of 4 confirmed/)).toBeInTheDocument()
    })

    it('adds the waitlist sentence only when the session has a maximum capacity', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({ data: detail({ maxCapacity: 8 }) })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      const { unmount } = renderPage()
      expect(await screen.findByText(/join the waitlist/)).toBeInTheDocument()
      unmount()
      cleanup()

      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}')
          return Promise.resolve({ data: detail({ maxCapacity: null }) })
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({ data: { items: [], nextCursor: null } })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()
      expect(await screen.findByText(/2 of 4 confirmed/)).toBeInTheDocument()
      expect(screen.queryByText(/join the waitlist/)).not.toBeInTheDocument()
    })
  })

  describe('RSVP group (ported from the flat page SessionStatusHero.test.tsx)', () => {
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

  describe('mobile sidebar stacking order (S3)', () => {
    it('gives every sidebar card its own order class, in the intended below-lg stacking order', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/sessions/{sessionId}') {
          const data = detail()
          data.roster[0].isWaitlisted = true
          return Promise.resolve({ data })
        }
        if (path === '/api/v1/sessions/{sessionId}/join-requests')
          return Promise.resolve({
            data: {
              items: [
                {
                  id: 'jr-1',
                  requesterId: '22222222-2222-2222-2222-222222222222',
                  status: 'pending',
                  createdAt: '2030-08-01T00:00:00Z',
                  requester: { ordinal: 0, displayName: 'Requester One' },
                },
              ],
              nextCursor: null,
            },
          })
        if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: null } })
        throw new Error(`Unexpected GET ${path}`)
      })
      renderPage()

      await screen.findByText('Raid night')
      await screen.findByText('Requester One')

      function headingOrderOf(name: string | RegExp) {
        return screen
          .getByRole('heading', { name })
          .closest('section')?.className.match(/\border-(\d+)\b/)?.[1]
      }

      const summaryOrder = screen
        .getByTestId('session-summary-card')
        .className.match(/\border-(\d+)\b/)?.[1]
      const rosterHeadingOrder = screen
        .getByRole('heading', { name: /Roster/ })
        .closest('section')?.className.match(/\border-(\d+)\b/)?.[1]
      const waitlistOrder = headingOrderOf(/^Waitlist/)
      const joinRequestsOrder = headingOrderOf(/^Join requests/)
      const notificationsOrder = headingOrderOf('Notifications for this session')
      const dangerOrder = headingOrderOf('Danger zone')

      // Every card has an explicit order class (none silently falls back to order:0).
      for (const value of [
        summaryOrder,
        rosterHeadingOrder,
        waitlistOrder,
        joinRequestsOrder,
        notificationsOrder,
        dangerOrder,
      ]) {
        expect(value).toBeDefined()
      }

      // Main RSVP content is a sibling `order-2` section outside this sidebar wrapper —
      // summary(1) -> RSVP column(2) -> roster(3) -> waitlist(3, same tier) -> join
      // requests(4) -> notifications(6) -> danger zone(last), and no card ties with the
      // RSVP column's order-2.
      expect(Number(summaryOrder)).toBe(1)
      expect(Number(rosterHeadingOrder)).toBeGreaterThan(2)
      expect(Number(waitlistOrder)).toBeGreaterThanOrEqual(Number(rosterHeadingOrder))
      expect(Number(joinRequestsOrder)).toBeGreaterThan(Number(waitlistOrder))
      expect(Number(notificationsOrder)).toBeGreaterThan(Number(joinRequestsOrder))
      expect(Number(dangerOrder)).toBeGreaterThan(Number(notificationsOrder))
    })
  })
})
