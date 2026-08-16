import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
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

import i18n from '../../i18n'
import { DAY_MS, startOfLocaleWeek, weekStartsOnIso } from '../availability/lovable/zone'
import { HomePage } from './HomePage'

function actionableInbox(
  overrides: Partial<{
    invitations: unknown[]
    joinRequests: unknown[]
    hasMoreInvitations: boolean
    hasMoreJoinRequests: boolean
  }> = {},
) {
  return {
    invitations: [],
    joinRequests: [],
    hasMoreInvitations: false,
    hasMoreJoinRequests: false,
    ...overrides,
  }
}

function summary(overrides: Record<string, unknown> = {}) {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2030-09-01T18:00:00Z',
    durationMinutes: 120,
    platform: 'steam',
    game: null,
    status: 'unconfirmed',
    isPrivate: false,
    maxCapacity: null,
    activeOccupancy: 1,
    organizer: { ordinal: 0, displayName: 'Organizer' },
    viewerRelation: 'inherited',
    ...overrides,
  }
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/app']}>
        <HomePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function defaultGetHandler(path: string) {
  if (path === '/api/v1/me/actionable')
    return Promise.resolve({ data: actionableInbox() })
  if (path === '/api/v1/sessions') return Promise.resolve({ data: { items: [], nextCursor: null } })
  if (path === '/api/v1/availability/suggestions')
    return Promise.resolve({ data: { suggestions: [], hasMore: false } })
  if (path === '/api/v1/polls') return Promise.resolve({ data: { items: [], nextCursor: null } })
  throw new Error(`Unexpected GET ${path}`)
}

beforeEach(() => {
  api.get.mockImplementation(defaultGetHandler)
})

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('HomePage', () => {
  it('renders the regions in decision order_D103', async () => {
    renderPage()

    await screen.findByTestId('home-region-actionable')

    const regions = screen
      .getAllByRole('heading', { level: 2 })
      .map((el) => el.closest('[role="region"]')?.getAttribute('data-testid'))

    expect(regions).toEqual([
      'home-region-actionable',
      'home-region-polls',
      'home-region-upcoming',
      'home-region-findatime',
      'home-region-discover',
    ])
  })

  it('distinguishes a pending invitation from a going rsvp_D103', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me/actionable') {
        return Promise.resolve({
          data: actionableInbox({
            invitations: [
              {
                session: summary({ id: 'session-invited', title: 'Needs a response', viewerRelation: 'invitee' }),
                invitedAt: '2030-08-01T00:00:00Z',
              },
              // A polluted row: already `Going` (or otherwise not awaiting a
              // response) never renders here — the client re-asserts
              // `viewerRelation == "invitee"` itself rather than trusting
              // the `invitations[]` array's name alone (§10.1). If this
              // filter regressed to "render everything in `invitations[]`
              // verbatim", this row would show up with respond actions too.
              {
                session: summary({ id: 'session-going', title: 'Already going', viewerRelation: 'participant' }),
                invitedAt: '2030-08-01T00:00:00Z',
              },
            ],
          }),
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const actionable = await screen.findByTestId('home-region-actionable')
    // A pending invitation shows RSVP actions and its own heading.
    expect(await within(actionable).findByText('Needs a response')).toBeInTheDocument()
    expect(within(actionable).getByRole('heading', { name: 'Invitations' })).toBeInTheDocument()
    expect(within(actionable).getByRole('button', { name: 'Going' })).toBeInTheDocument()
    expect(within(actionable).queryByRole('heading', { name: 'Join requests' })).not.toBeInTheDocument()
    // The polluted `participant`-relation row never renders, respond
    // actions included.
    expect(within(actionable).queryByText('Already going')).not.toBeInTheDocument()
  })

  it('names the requester on each join request row_D103', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me/actionable') {
        return Promise.resolve({
          data: actionableInbox({
            joinRequests: [
              {
                joinRequestId: 'jr-1',
                session: summary({ id: 'session-mine', title: 'My party', viewerRelation: 'organizer' }),
                requester: { ordinal: 0, displayName: 'Ada Requester' },
                requestedAt: '2030-08-01T00:00:00Z',
              },
            ],
          }),
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const actionable = await screen.findByTestId('home-region-actionable')
    expect(
      await within(actionable).findByText('Requested by Ada Requester'),
    ).toBeInTheDocument()
  })

  it('scopes RSVP pending state to the row being responded to, not every invitation row (Wave 6)', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me/actionable') {
        return Promise.resolve({
          data: actionableInbox({
            invitations: [
              {
                session: summary({ id: 'session-a', title: 'Party A', viewerRelation: 'invitee' }),
                invitedAt: '2030-08-01T00:00:00Z',
              },
              {
                session: summary({ id: 'session-b', title: 'Party B', viewerRelation: 'invitee' }),
                invitedAt: '2030-08-01T00:00:00Z',
              },
            ],
          }),
        })
      }
      return defaultGetHandler(path)
    })
    api.put.mockImplementation(() => new Promise(() => {}))
    renderPage()

    const actionable = await screen.findByTestId('home-region-actionable')
    const rowA = (await within(actionable).findByText('Party A')).closest('li')!
    const rowB = within(actionable).getByText('Party B').closest('li')!

    fireEvent.click(within(rowA).getByRole('button', { name: 'Going' }))

    await waitFor(() =>
      expect(within(rowA).getByRole('button', { name: 'Going' })).toBeDisabled(),
    )
    expect(within(rowB).getByRole('button', { name: 'Going' })).not.toBeDisabled()
  })

  it('computes the eyebrow as today plus its locale week range, Sunday-first for en_D103', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2030, 8, 4, 12, 0, 0)) // Wednesday 4 Sep 2030, local time
    try {
      renderPage()
      const heading = await screen.findByRole('heading', { level: 1, name: 'Home' })
      const eyebrow = heading.parentElement?.querySelector('p')

      // Same "fake local" encoding `HomePage.formatEyebrow` uses: the local
      // wall-clock fields read back through `timeZone: 'UTC'` formatters.
      const zonedNow = new Date(Date.UTC(2030, 8, 4, 12, 0, 0))
      const weekStart = startOfLocaleWeek(zonedNow, weekStartsOnIso('en'))
      const weekEnd = new Date(weekStart.getTime() + 6 * DAY_MS)

      const today = new Intl.DateTimeFormat('en', {
        weekday: 'short',
        day: 'numeric',
        month: 'short',
        timeZone: 'UTC',
      }).format(zonedNow)
      const weekRange = new Intl.DateTimeFormat('en', {
        day: 'numeric',
        month: 'short',
        timeZone: 'UTC',
      }).formatRange(weekStart, weekEnd)

      // en resolves Sunday-first (D-103): the displayed week starts on the
      // Sunday before the Wednesday anchor, not the Monday.
      expect(weekStart.getUTCDate()).toBe(1)
      expect(eyebrow?.textContent).toBe(`${today} · week of ${weekRange}`)
    } finally {
      vi.useRealTimers()
    }
  })

  it('computes the eyebrow week range Monday-first for es, matching the calendar convention_D103', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date(2030, 8, 4, 12, 0, 0)) // Wednesday 4 Sep 2030, local time
    await i18n.changeLanguage('es')
    try {
      renderPage()
      const heading = await screen.findByRole('heading', { level: 1, name: 'Inicio' })
      const eyebrow = heading.parentElement?.querySelector('p')

      const zonedNow = new Date(Date.UTC(2030, 8, 4, 12, 0, 0))
      const weekStart = startOfLocaleWeek(zonedNow, weekStartsOnIso('es'))
      const weekEnd = new Date(weekStart.getTime() + 6 * DAY_MS)

      const today = new Intl.DateTimeFormat('es', {
        weekday: 'short',
        day: 'numeric',
        month: 'short',
        timeZone: 'UTC',
      }).format(zonedNow)
      const weekRange = new Intl.DateTimeFormat('es', {
        day: 'numeric',
        month: 'short',
        timeZone: 'UTC',
      }).formatRange(weekStart, weekEnd)

      // es resolves Monday-first (D-103): the displayed week starts on the
      // Monday of the Wednesday anchor's week.
      expect(weekStart.getUTCDate()).toBe(2)
      expect(eyebrow?.textContent).toBe(`${today} · semana del ${weekRange}`)
    } finally {
      await i18n.changeLanguage('en')
      vi.useRealTimers()
    }
  })

  it('shows the maybe count alongside available on each find-a-time preview row, not available alone', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/suggestions') {
        return Promise.resolve({
          data: {
            suggestions: [
              {
                start: '2030-08-01T10:00:00Z',
                end: '2030-08-01T12:00:00Z',
                available: [{ ordinal: 0, displayName: 'Friend' }],
                maybe: [
                  { ordinal: 0, displayName: 'Maybe One' },
                  { ordinal: 1, displayName: 'Maybe Two' },
                ],
              },
            ],
            hasMore: false,
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const list = await screen.findByTestId('home-suggestions-list')
    expect(within(list).getByText(/1 available/)).toBeInTheDocument()
    expect(within(list).getByText(/2 maybe/)).toBeInTheDocument()
  })

  it('shows the discover region cards with game/platform/occupancy, not title/status/startTime alone', async () => {
    api.get.mockImplementation((path: string, options?: { params?: { query?: { scope?: string } } }) => {
      if (path === '/api/v1/sessions' && options?.params?.query?.scope === 'discover') {
        return Promise.resolve({
          data: {
            items: [
              summary({
                id: 'session-discover',
                title: 'Come join us',
                game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
                maxCapacity: 6,
                activeOccupancy: 2,
              }),
            ],
            nextCursor: null,
          },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const discoverRegion = await screen.findByTestId('home-region-discover')
    expect(await within(discoverRegion).findByText('Deep Rock Galactic')).toBeInTheDocument()
    expect(within(discoverRegion).getByText('2/6 going')).toBeInTheDocument()
  })

  it('shows five suggestions with see all_D110_D116', async () => {
    const suggestions = Array.from({ length: 5 }, (_, index) => ({
      start: `2030-08-0${index + 1}T10:00:00Z`,
      end: `2030-08-0${index + 1}T12:00:00Z`,
      available: [{ ordinal: 0, displayName: 'Friend' }],
      maybe: [],
    }))
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/availability/suggestions') {
        return Promise.resolve({ data: { suggestions, hasMore: true } })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const list = await screen.findByTestId('home-suggestions-list')
    expect(within(list).getAllByRole('listitem')).toHaveLength(5)
    const findATime = await screen.findByTestId('home-region-findatime')
    expect(within(findATime).getByRole('link', { name: 'See all' })).toHaveAttribute(
      'href',
      '/find-a-time',
    )
  })

  it('renders the discoverable region from the first discover page_D149', async () => {
    api.get.mockImplementation((path: string, options?: { params?: { query?: { scope?: string } } }) => {
      if (path === '/api/v1/sessions' && options?.params?.query?.scope === 'discover') {
        return Promise.resolve({
          data: { items: [summary({ id: 'session-discover', title: 'Come join us' })], nextCursor: null },
        })
      }
      return defaultGetHandler(path)
    })
    renderPage()

    const discoverRegion = await screen.findByTestId('home-region-discover')
    expect(await within(discoverRegion).findByText('Come join us')).toBeInTheDocument()
    expect(
      within(discoverRegion).getByRole('link', { name: /Come join us/ }),
    ).toHaveAttribute('href', '/sessions/session-discover')
  })

  describe('region 2 — polls needing the viewer\'s vote (D-103, S7 fill)', () => {
    function pollSummary(overrides: Record<string, unknown> = {}) {
      return {
        id: 'poll-1',
        organizerId: 'organizer-1',
        title: 'Raid night poll',
        status: 'polling',
        isPrivate: false,
        deadline: new Date(Date.now() + 60 * 60_000).toISOString(),
        selectionWindowEndsAt: new Date(Date.now() + 48 * 60 * 60_000).toISOString(),
        createdAt: '2030-01-01T00:00:00Z',
        optionCount: 3,
        ...overrides,
      }
    }

    it('renders a still-polling involved poll with a link to its detail page', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/polls') {
          return Promise.resolve({ data: { items: [pollSummary()], nextCursor: null } })
        }
        return defaultGetHandler(path)
      })
      renderPage()

      const pollsRegion = await screen.findByTestId('home-region-polls')
      expect(await within(pollsRegion).findByText('Raid night poll')).toBeInTheDocument()
      expect(
        within(pollsRegion).getByRole('link', { name: /Raid night poll/ }),
      ).toHaveAttribute('href', '/polls/poll-1')
    })

    it('excludes an involved poll whose window has already closed, honestly disclosing the polling-only predicate', async () => {
      api.get.mockImplementation((path: string) => {
        if (path === '/api/v1/polls') {
          return Promise.resolve({
            data: {
              items: [
                pollSummary({
                  id: 'poll-closed',
                  title: 'Already closed',
                  status: 'closed',
                  deadline: new Date(Date.now() - 60_000).toISOString(),
                }),
              ],
              nextCursor: null,
            },
          })
        }
        return defaultGetHandler(path)
      })
      renderPage()

      const pollsRegion = await screen.findByTestId('home-region-polls')
      expect(
        await within(pollsRegion).findByText('No open polls right now.'),
      ).toBeInTheDocument()
      expect(screen.queryByText('Already closed')).not.toBeInTheDocument()
    })

    it('renders the empty state with no involved polls', async () => {
      renderPage()

      const pollsRegion = await screen.findByTestId('home-region-polls')
      expect(
        await within(pollsRegion).findByText('No open polls right now.'),
      ).toBeInTheDocument()
    })
  })
})
