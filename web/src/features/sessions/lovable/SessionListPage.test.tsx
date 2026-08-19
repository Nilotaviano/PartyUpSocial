import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

import '../../../i18n'
import { SessionListPage } from './SessionListPage'

function session(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2020-01-01T00:00:00Z',
    durationMinutes: 120,
    platform: 'Steam',
    game: null,
    status: 'confirmed',
    isPrivate: false,
    maxCapacity: 8,
    activeOccupancy: 2,
    organizer: { ordinal: 0, displayName: 'Alex Chen', avatar: null },
    viewerRelation: 'organizer',
    ...overrides,
  }
}

function renderPage(scope: 'upcoming' | 'history') {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <SessionListPage scope={scope} />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionListPage', () => {
  it('badges a started, not-yet-ended session as in progress on the upcoming list_D095', async () => {
    const now = Date.now()
    api.get.mockResolvedValue({
      data: {
        items: [
          session({
            startTime: new Date(now - 60_000).toISOString(),
            durationMinutes: 60,
          }),
        ],
        nextCursor: null,
      },
    })
    renderPage('upcoming')

    expect(await screen.findByText('In progress')).toBeInTheDocument()
  })

  it('renders the history list with no in-progress badge and no create-session action_D095', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/sessions/history/stats') {
        return Promise.resolve({
          data: { completedCount: 0, hoursTogether: 0, topGame: null, topMates: [] },
        })
      }
      return Promise.resolve({
        data: { items: [session({ status: 'completed' })], nextCursor: null },
      })
    })
    renderPage('history')

    expect(await screen.findByText('Raid night')).toBeInTheDocument()
    expect(screen.queryByText('In progress')).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'New session' })).not.toBeInTheDocument()
  })

  it('maps the notApplicable wire platform value to a localized "Other" label, not the raw wire value', async () => {
    api.get.mockResolvedValue({
      data: { items: [session({ platform: 'notApplicable' })], nextCursor: null },
    })
    renderPage('upcoming')

    await screen.findByText('Raid night')
    expect(screen.queryByText('notApplicable')).not.toBeInTheDocument()
    expect(screen.getByText('Other')).toBeInTheDocument()
  })

  it('loads the next cursor page and appends results_D095', async () => {
    api.get.mockImplementation(
      (path: string, options?: { params: { query: { cursor?: string } } }) => {
        if (path === '/api/v1/me') {
          return Promise.resolve({ data: {} })
        }
        const cursor = options?.params.query.cursor
        if (!cursor) {
          return Promise.resolve({
            data: { items: [session({ id: 'session-1', title: 'First' })], nextCursor: 'page-2' },
          })
        }
        return Promise.resolve({
          data: { items: [session({ id: 'session-2', title: 'Second' })], nextCursor: null },
        })
      },
    )
    renderPage('upcoming')

    expect(await screen.findByText('First')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }))

    await waitFor(() => expect(screen.getByText('Second')).toBeInTheDocument())
    expect(screen.getByText('First')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Load more' })).not.toBeInTheDocument()
  })

  it('focuses the page h1 on mount_D139', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })
    renderPage('upcoming')

    const heading = await screen.findByRole('heading', { level: 1 })
    await waitFor(() => expect(heading).toHaveFocus())
  })

  it('shows the empty-upcoming state', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })
    renderPage('upcoming')

    expect(await screen.findByText('No upcoming sessions.')).toBeInTheDocument()
  })

  it('renders session start times in the viewer profile time zone rather than the browser zone_D038_D046', async () => {
    const startTime = '2030-06-15T23:00:00Z'
    const format = (timeZone?: string) =>
      new Intl.DateTimeFormat('en', {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone,
      }).format(new Date(startTime))
    const hostRender = format()
    // A profile zone whose rendering provably differs from the host zone, so the
    // assertion fails if the component silently falls back to the browser zone.
    const profileZone = ['Asia/Tokyo', 'Pacific/Honolulu'].find(
      (zone) => format(zone) !== hostRender,
    )!
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: { timeZone: profileZone } })
      }
      return Promise.resolve({
        data: { items: [session({ startTime })], nextCursor: null },
      })
    })
    renderPage('upcoming')

    expect(await screen.findByText(format(profileZone))).toBeInTheDocument()
  })

  it('shows a session status pill with its caller-resolved label, not color alone_D139', async () => {
    api.get.mockResolvedValue({
      data: { items: [session({ status: 'unconfirmed' })], nextCursor: null },
    })
    renderPage('upcoming')

    expect(await screen.findByText('Waiting for players')).toBeInTheDocument()
  })

  it('shows occupancy as a text-coded chip against the capacity limit', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [session({ maxCapacity: 8, activeOccupancy: 3 })],
        nextCursor: null,
      },
    })
    renderPage('upcoming')

    expect(await screen.findByText('3/8 going')).toBeInTheDocument()
  })

  it('shows occupancy without a capacity denominator for an unlimited session', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [session({ maxCapacity: null, activeOccupancy: 5 })],
        nextCursor: null,
      },
    })
    renderPage('upcoming')

    expect(await screen.findByText('5 going')).toBeInTheDocument()
  })

  it('shows the organizer identity via name and avatar disc', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [session({ organizer: { ordinal: 0, displayName: 'Alex Chen', avatar: null } })],
        nextCursor: null,
      },
    })
    renderPage('upcoming')

    expect(await screen.findByText('Alex Chen')).toBeInTheDocument()
  })

  it('masks the organizer identity when the wire withholds it, never leaking a real name_D076', async () => {
    api.get.mockResolvedValue({
      data: {
        items: [
          session({ organizerId: null, organizer: { ordinal: 0, displayName: null, avatar: null } }),
        ],
        nextCursor: null,
      },
    })
    renderPage('upcoming')

    expect(await screen.findByText('Anonymous User')).toBeInTheDocument()
  })

  it('links to /sessions/new to create a session on the upcoming scope', async () => {
    api.get.mockResolvedValue({ data: { items: [], nextCursor: null } })
    renderPage('upcoming')

    expect(
      (await screen.findAllByRole('link', { name: 'New session' }))[0],
    ).toHaveAttribute('href', '/sessions/new')
  })
})
