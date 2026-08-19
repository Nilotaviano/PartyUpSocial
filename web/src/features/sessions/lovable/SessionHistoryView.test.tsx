import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn() }))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get },
  isApiError: () => false,
}))

import '../../../i18n'
import { SessionListPage } from './SessionListPage'

type Json = Record<string, unknown>

const VIEWER_ID = 'viewer-1'

function player(ordinal: number, displayName: string | null, avatar: Json | null = null) {
  return { ordinal, displayName, avatar }
}

function historyItem(overrides: Json = {}): Json {
  return {
    id: 'session-1',
    organizerId: 'organizer-1',
    title: 'Raid night',
    startTime: '2026-08-04T20:00:00Z',
    durationMinutes: 150,
    platform: 'steam',
    game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
    status: 'completed',
    maxCapacity: 6,
    activeOccupancy: 3,
    organizer: player(0, 'Vera'),
    viewerRelation: 'participant',
    visibility: 'public',
    viaGroup: null,
    sharedWithGroup: false,
    players: [player(0, 'Vera'), player(1, 'Frankie')],
    ...overrides,
  }
}

const emptyStats = {
  completedCount: 0,
  hoursTogether: 0,
  topGame: null,
  topMates: [],
}

function mockApi({
  items,
  stats = emptyStats,
  profile = { userId: VIEWER_ID, displayName: 'Vera', timeZone: 'UTC' },
  pendingList = false,
}: {
  items: Json[]
  stats?: Json
  profile?: Json
  pendingList?: boolean
}) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/me') return Promise.resolve({ data: profile })
    if (path === '/api/v1/sessions/history/stats') return Promise.resolve({ data: stats })
    if (pendingList) return new Promise(() => {})
    return Promise.resolve({ data: { items, nextCursor: null } })
  })
}

function renderHistory() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter>
        <SessionListPage scope="history" />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('SessionHistoryView', () => {
  it('groups entries by month with the newest month first_D221', async () => {
    mockApi({
      items: [
        historyItem({ id: 'a', title: 'August night', startTime: '2026-08-04T20:00:00Z' }),
        historyItem({ id: 'b', title: 'July night', startTime: '2026-07-30T20:00:00Z' }),
        historyItem({ id: 'c', title: 'Later August', startTime: '2026-08-20T20:00:00Z' }),
      ],
    })
    renderHistory()

    const months = await screen.findAllByRole('heading', { level: 2 })
    const monthTitles = months.map((heading) => heading.textContent)
    expect(monthTitles).toContain('August 2026')
    expect(monthTitles.indexOf('August 2026')).toBeLessThan(monthTitles.indexOf('July 2026'))

    const entryTitles = screen
      .getAllByRole('heading', { level: 3 })
      .map((heading) => heading.textContent)
    expect(entryTitles).toEqual(['Later August', 'August night', 'July night'])
  })

  it('renders a canceled entry struck through, tagged, and without a duration_D221', async () => {
    mockApi({
      items: [historyItem({ id: 'a', title: 'Called off', status: 'canceled' })],
    })
    renderHistory()

    const title = await screen.findByRole('heading', { level: 3, name: 'Called off' })
    expect(title.className).toContain('line-through')
    expect(screen.getByText('Canceled')).toBeInTheDocument()
    expect(screen.queryByText('2h 30m')).not.toBeInTheDocument()
  })

  it('badges a session the viewer organized_D221', async () => {
    mockApi({
      items: [
        historyItem({ id: 'a', title: 'Mine', organizerId: VIEWER_ID }),
        historyItem({ id: 'b', title: 'Theirs', organizerId: 'someone-else' }),
      ],
    })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'Mine' })
    expect(screen.getAllByText('You organized')).toHaveLength(1)
    expect(screen.getAllByText('2h 30m')).toHaveLength(2)
  })

  it('shows the game under a differently-titled entry_D221', async () => {
    mockApi({
      items: [
        historyItem({
          id: 'a',
          title: 'Raid night',
          game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
        }),
      ],
    })
    renderHistory()

    const title = await screen.findByRole('heading', { level: 3, name: 'Raid night' })
    const entry = title.closest('li') ?? title.parentElement!.parentElement!
    expect(within(entry).getByText('Deep Rock Galactic')).toBeInTheDocument()
    expect(within(entry).queryAllByText('Raid night')).toHaveLength(1)
  })

  it('renders the title once only when it matches the game name_D221', async () => {
    mockApi({
      items: [
        historyItem({
          id: 'a',
          title: 'Deep Rock Galactic',
          game: { name: 'Deep Rock Galactic', steamAppId: 548430 },
        }),
      ],
    })
    renderHistory()

    const title = await screen.findByRole('heading', { level: 3, name: 'Deep Rock Galactic' })
    const entry = title.closest('li') ?? title.parentElement!.parentElement!
    expect(within(entry).getAllByText('Deep Rock Galactic')).toHaveLength(1)
  })

  it('renders the four recap facts over the whole history_D222', async () => {
    mockApi({
      items: [historyItem()],
      stats: {
        completedCount: 12,
        hoursTogether: 27,
        topGame: { name: 'Deep Rock Galactic', times: 5 },
        topMates: [
          { player: player(1, 'Frankie'), times: 4 },
          { player: player(9, null), times: 2 },
        ],
      },
    })
    renderHistory()

    expect(await screen.findByText("Nights you've already had")).toBeInTheDocument()
    expect(screen.getByText('12')).toBeInTheDocument()
    expect(screen.getByText('27 hours')).toBeInTheDocument()
    expect(screen.getByText('5 nights')).toBeInTheDocument()
    const recap = screen.getByRole('region', { name: "Nights you've already had" })
    expect(within(recap).getByText('Frankie')).toBeInTheDocument()
    // A withheld mate keeps its place under the ordinary masked label_D076.
    expect(within(recap).getByText('Anonymous User')).toBeInTheDocument()
  })

  it('hides the recap entirely when no session has completed_D222', async () => {
    mockApi({ items: [historyItem({ status: 'canceled' })] })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'Raid night' })
    expect(screen.queryByText("Nights you've already had")).not.toBeInTheDocument()
  })

  it('narrows loaded entries by search text_D223', async () => {
    mockApi({
      items: [
        historyItem({ id: 'a', title: 'Deep dive', game: { name: 'Deep Rock Galactic' } }),
        historyItem({
          id: 'b',
          title: 'Board games',
          game: null,
          players: [player(0, 'Vera'), player(1, 'Sam')],
        }),
      ],
    })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'Deep dive' })
    fireEvent.change(screen.getByLabelText('Search past sessions'), { target: { value: 'sam' } })

    await waitFor(() =>
      expect(screen.queryByRole('heading', { level: 3, name: 'Deep dive' })).not.toBeInTheDocument(),
    )
    expect(screen.getByRole('heading', { level: 3, name: 'Board games' })).toBeInTheDocument()
  })

  it('narrows loaded entries by a single-select game chip_D223', async () => {
    mockApi({
      items: [
        historyItem({ id: 'a', title: 'Rock night', game: { name: 'Deep Rock Galactic' } }),
        historyItem({ id: 'b', title: 'Valheim night', game: { name: 'Valheim' } }),
      ],
    })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'Rock night' })
    const chip = screen.getByRole('button', { name: 'Valheim', pressed: false })
    fireEvent.click(chip)

    await waitFor(() =>
      expect(screen.queryByRole('heading', { level: 3, name: 'Rock night' })).not.toBeInTheDocument(),
    )
    expect(screen.getByRole('button', { name: 'Valheim', pressed: true })).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 3, name: 'Valheim night' })).toBeInTheDocument()
  })

  it('narrows loaded entries by a single-select played-with chip_D223', async () => {
    mockApi({
      items: [
        historyItem({ id: 'a', title: 'With Frankie', players: [player(0, 'Vera'), player(1, 'Frankie')] }),
        historyItem({ id: 'b', title: 'With Sam', players: [player(0, 'Vera'), player(2, 'Sam')] }),
      ],
    })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'With Frankie' })
    // The viewer's own name is never offered as a played-with chip.
    expect(screen.queryByRole('button', { name: 'Vera' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sam' }))

    await waitFor(() =>
      expect(
        screen.queryByRole('heading', { level: 3, name: 'With Frankie' }),
      ).not.toBeInTheDocument(),
    )
    expect(screen.getByRole('heading', { level: 3, name: 'With Sam' })).toBeInTheDocument()
  })

  it('composes a chip and the search box with AND_D223', async () => {
    mockApi({
      items: [
        historyItem({
          id: 'a',
          title: 'Rock with Sam',
          game: { name: 'Deep Rock Galactic' },
          players: [player(0, 'Vera'), player(1, 'Sam')],
        }),
        historyItem({
          id: 'b',
          title: 'Rock alone',
          game: { name: 'Deep Rock Galactic' },
          players: [player(0, 'Vera')],
        }),
        historyItem({
          id: 'c',
          title: 'Valheim with Sam',
          game: { name: 'Valheim' },
          players: [player(0, 'Vera'), player(2, 'Sam')],
        }),
      ],
    })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'Rock with Sam' })
    fireEvent.click(screen.getByRole('button', { name: 'Deep Rock Galactic' }))
    fireEvent.change(screen.getByLabelText('Search past sessions'), { target: { value: 'sam' } })

    await waitFor(() =>
      expect(screen.queryByRole('heading', { level: 3, name: 'Rock alone' })).not.toBeInTheDocument(),
    )
    expect(
      screen.queryByRole('heading', { level: 3, name: 'Valheim with Sam' }),
    ).not.toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 3, name: 'Rock with Sam' })).toBeInTheDocument()
  })

  it('never renders a masked player as a name, a chip, or a search match_D076', async () => {
    mockApi({
      items: [
        historyItem({
          id: 'a',
          title: 'Blocked pair night',
          players: [player(0, 'Vera'), player(7, null)],
        }),
      ],
    })
    renderHistory()

    await screen.findByRole('heading', { level: 3, name: 'Blocked pair night' })
    expect(screen.getByText('2 played')).toBeInTheDocument()
    expect(screen.queryByText('Anonymous User')).not.toBeInTheDocument()
    // Only legible, non-viewer names become played-with chips.
    expect(screen.getAllByRole('button', { pressed: false })).toHaveLength(1)
  })

  it('shows a loading skeleton before the first history page resolves', async () => {
    mockApi({ items: [], pendingList: true })
    renderHistory()

    expect(await screen.findByTestId('history-skeleton')).toBeInTheDocument()
    expect(screen.getByText('Loading your session history')).toBeInTheDocument()
  })

  it('shows the friendlier empty state with no recap when history is empty', async () => {
    mockApi({ items: [] })
    renderHistory()

    expect(await screen.findByText('Your game nights will land here')).toBeInTheDocument()
    expect(screen.queryByText("Nights you've already had")).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'New session' })).toHaveAttribute(
      'href',
      '/sessions/new',
    )
  })

  it('groups by the start in the viewer profile zone, not the browser zone_D038_D046', async () => {
    mockApi({
      // 2026-08-01T02:00Z is still July 31 in Los Angeles.
      items: [historyItem({ id: 'a', startTime: '2026-08-01T02:00:00Z' })],
      profile: { userId: VIEWER_ID, displayName: 'Vera', timeZone: 'America/Los_Angeles' },
    })
    renderHistory()

    const month = await screen.findByRole('heading', { level: 2, name: /2026/ })
    expect(month).toHaveTextContent('July 2026')
  })
})
