import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }))
const navigateSpy = vi.hoisted(() => vi.fn())

vi.mock('../../../api/client', () => ({
  client: { GET: api.get, POST: api.post },
  isApiError: (error: unknown) =>
    Boolean(error) && typeof error === 'object' && 'code' in (error as object),
}))

vi.mock('react-router', async () => {
  const actual = await vi.importActual<typeof import('react-router')>('react-router')
  return { ...actual, useNavigate: () => navigateSpy }
})

import i18n from '../../../i18n'
import {
  DAY_MS,
  fakeLocalToInstant,
  instantToFakeLocal,
  startOfLocaleWeek,
  weekStartsOnIso,
} from '../../availability/lovable/zone'
import { PollCreatePage } from './PollCreatePage'

function renderPage(initialEntry = '/polls/new') {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={[initialEntry]}>
        <PollCreatePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

// The grid's per-30-minute cells carry the `candidate-slot-{day}-{minute}`
// testid contract the browser tests click; reading them in DOM order gives a
// stable, deterministic pair of slots to place candidates on.
async function findGridSlots() {
  const grid = await screen.findByTestId('candidate-grid')
  await within(grid).findAllByRole('gridcell')
  return Array.from(
    grid.querySelectorAll<HTMLElement>('[data-testid^="candidate-slot-"]'),
  )
}

async function selectTwoGridSlots() {
  const slots = await findGridSlots()
  fireEvent.click(slots[0])
  fireEvent.click(slots[1])
}

const emptyCalendarView = {
  myAvailability: { occurrences: [], merged: [], range: null, series: [] },
  friendAvailability: [] as unknown[],
  sessions: [],
  pollOptions: [],
}

function mockGet({
  friends = { items: [] } as unknown,
  catalogAvailable = false,
  catalogResults = [] as { appId: number; name: string }[],
  timeZone = 'UTC',
  calendarView = emptyCalendarView as unknown,
} = {}) {
  api.get.mockImplementation((path: string) => {
    if (path === '/api/v1/steam-catalog') {
      return Promise.resolve({
        data: { catalogAvailable, results: catalogResults },
      })
    }
    if (path === '/api/v1/me') {
      return Promise.resolve({ data: { timeZone } })
    }
    if (path === '/api/v1/calendar') {
      return Promise.resolve({ data: calendarView })
    }
    return Promise.resolve({ data: friends })
  })
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('PollCreatePage (lovable)', () => {
  it('focuses the page h1 on mount_D139', async () => {
    mockGet()
    renderPage()

    const heading = await screen.findByRole('heading', { level: 1 })
    await waitFor(() => expect(heading).toHaveFocus())
  })

  it('defaults the deadline choice to 24 hours_D070', async () => {
    mockGet()
    renderPage()

    expect(await screen.findByRole('radio', { name: '24 hours' })).toBeChecked()
  })

  it('requires at least two options before submit_D131', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      (await screen.findAllByText('Add at least two candidate times.')).length,
    ).toBeGreaterThan(0)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('shows the invitee avatar without changing the checkbox accessible name, and toggles by click and keyboard_D050', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/steam-catalog') {
        return Promise.resolve({ data: { catalogAvailable: false, results: [] } })
      }
      if (path === '/api/v1/calendar') {
        return Promise.resolve({ data: emptyCalendarView })
      }
      return Promise.resolve({
        data: {
          items: [
            {
              friendshipId: 'friendship-1',
              userId: 'user-1',
              username: 'friend.one',
              displayName: 'Friend One',
              avatar: { kind: 'emoji', emoji: '🎮', avatarSourceId: null, imgUrl: null },
              platforms: [],
            },
          ],
        },
      })
    })
    renderPage()

    const checkbox = await screen.findByRole('checkbox', { name: 'Friend One (@friend.one)' })
    const label = checkbox.closest('label')
    expect(label?.querySelector('[aria-hidden="true"]')).not.toBeNull()

    expect(checkbox).not.toBeChecked()
    fireEvent.click(label as HTMLElement)
    expect(checkbox).toBeChecked()

    // Native keyboard activation (Space/Enter on a focused input) dispatches
    // a click event on the input itself; jsdom does not synthesize this from
    // keydown, so a direct click on the focused control is the faithful proxy.
    checkbox.focus()
    fireEvent.click(checkbox)
    expect(checkbox).not.toBeChecked()
  })

  it('distinguishes loading, failed, and empty invitee-checklist states (Wave 6)', async () => {
    let resolveFriends: (() => void) | undefined
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/steam-catalog') {
        return Promise.resolve({ data: { catalogAvailable: false, results: [] } })
      }
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: 'UTC' } })
      if (path === '/api/v1/calendar') return Promise.resolve({ data: emptyCalendarView })
      if (path === '/api/v1/friend-groups') {
        return Promise.resolve({ data: { owned: [], memberOf: [], invites: [] } })
      }
      return new Promise((resolve) => {
        resolveFriends = () => resolve({ data: { items: [] } })
      })
    })
    renderPage()

    expect(await screen.findByText('Loading...')).toBeInTheDocument()

    resolveFriends?.()
    expect(await screen.findByText('You have no friends to invite yet.')).toBeInTheDocument()
  })

  it('surfaces a friends-list load failure distinctly from an empty checklist (Wave 6)', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/steam-catalog') {
        return Promise.resolve({ data: { catalogAvailable: false, results: [] } })
      }
      if (path === '/api/v1/me') return Promise.resolve({ data: { timeZone: 'UTC' } })
      if (path === '/api/v1/calendar') return Promise.resolve({ data: emptyCalendarView })
      if (path === '/api/v1/friends') return Promise.reject(new Error('boom'))
      return Promise.resolve({ data: { items: [] } })
    })
    renderPage()

    expect(await screen.findByText("Couldn't load your friends list.")).toBeInTheDocument()
    expect(screen.queryByText('You have no friends to invite yet.')).not.toBeInTheDocument()
  })

  it('never produces a duplicate option nor a validation error for the same candidate slot twice_D080', async () => {
    mockGet()
    renderPage()

    const slots = await findGridSlots()
    fireEvent.click(slots[0])
    expect(await screen.findByRole('group', { name: 'Candidate times' })).toBeInTheDocument()
    within(screen.getByRole('group', { name: 'Candidate times' })).getByRole('listitem')

    // Clicking the very same slot again removes it — the instant can never
    // appear twice, and no validation error is ever raised for it.
    fireEvent.click(slots[0])
    expect(
      within(screen.getByRole('group', { name: 'Candidate times' })).queryByRole('listitem'),
    ).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('rejects a non-private poll with zero invited friends_D125', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      (
        await screen.findAllByText('A non-private poll needs at least one invited friend.')
      ).length,
    ).toBeGreaterThan(0)
    expect(api.post).not.toHaveBeenCalled()
  })

  it('submits every eventual session detail alongside the poll options_D154', async () => {
    mockGet()
    api.post.mockResolvedValue({
      data: { poll: { id: 'poll-1' }, guestLinks: [] },
    })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.change(screen.getByLabelText('Notes'), {
      target: { value: 'Bring snacks' },
    })
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    const [, options] = api.post.mock.calls[0] as [string, { body: Record<string, unknown> }]
    expect(options.body).toMatchObject({
      title: 'Weekend raid',
      durationMinutes: 120,
      notes: 'Bring snacks',
      minAttendance: 4,
      maxCapacity: 4,
      visibility: 'private',
      deadlineChoice: 'hours24',
    })
    expect(options.body.optionStartTimes).toHaveLength(2)
    expect(navigateSpy).toHaveBeenCalledWith('/polls/poll-1')
  })

  it('does not render a catalog/free-text radio choice for the game field_D176', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Platform'), { target: { value: 'Steam' } })

    expect(screen.queryByRole('radio', { name: /^(Steam|PlayStation)/ })).not.toBeInTheDocument()
    expect(
      screen.getByRole('combobox', { name: 'Search the Steam catalog' }),
    ).toBeInTheDocument()
  })

  it('submits steamAppId as null when the free-typed game name is never selected from the catalog_D176_D194', async () => {
    mockGet()
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('selecting a catalog result fills name+appId and submits both_D194', async () => {
    mockGet({ catalogAvailable: true, catalogResults: [{ appId: 730, name: 'Counter-Strike 2' }] })
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'counter' },
    })
    fireEvent.mouseDown(await screen.findByRole('option', { name: 'Counter-Strike 2' }))
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: '730' },
          }),
        }),
      ),
    )
  })

  it('retyping the name after selecting a catalog result clears the stale appId_D194', async () => {
    mockGet({ catalogAvailable: true, catalogResults: [{ appId: 730, name: 'Counter-Strike 2' }] })
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'counter' },
    })
    fireEvent.mouseDown(await screen.findByRole('option', { name: 'Counter-Strike 2' }))

    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Deep Rock Galactic' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Deep Rock Galactic', steamAppId: null },
          }),
        }),
      ),
    )
  })

  it('degrades to the manual App ID field and submits the typed digits when the catalog is unavailable_D176_D194', async () => {
    mockGet()
    api.post.mockResolvedValue({ data: { poll: { id: 'poll-1' }, guestLinks: [] } })
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: '730' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/polls',
        expect.objectContaining({
          body: expect.objectContaining({
            game: { name: 'Counter-Strike 2', steamAppId: '730' },
          }),
        }),
      ),
    )
  })

  it('extracts the app id from a pasted Steam store URL in the degraded arm_D176_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    const appIdField = await screen.findByLabelText('Steam app ID')

    fireEvent.change(appIdField, {
      target: { value: 'https://store.steampowered.com/app/730/CounterStrike_2/' },
    })

    expect(screen.getByLabelText('Steam app ID')).toHaveValue('730')
  })

  it('rejects a non-digit Steam app id and blocks submission in the degraded arm_D176_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'Counter-Strike 2' },
    })
    fireEvent.change(await screen.findByLabelText('Steam app ID'), { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('requires a game name when a Steam app id is typed with the name left blank in the degraded arm_F1_D194', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })
    await selectTwoGridSlots()
    fireEvent.click(screen.getByRole('radio', { name: 'Private' }))
    fireEvent.change(screen.getByLabelText('Platform'), { target: { value: 'Steam' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Search the Steam catalog' }), {
      target: { value: 'aa' },
    })
    const nameField = await screen.findByLabelText('Enter a game name')
    fireEvent.change(nameField, { target: { value: '' } })
    fireEvent.change(screen.getByLabelText('Steam app ID'), { target: { value: '730' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create poll' }))

    expect(
      await screen.findByText('There were problems with your submission'),
    ).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()
  })

  it('warns before discarding dirty create-form values_D162', async () => {
    mockGet()
    renderPage()

    fireEvent.change(await screen.findByLabelText('Title'), {
      target: { value: 'Weekend raid' },
    })

    const cancelTrigger = screen.getByRole('button', { name: 'Cancel' })
    fireEvent.click(cancelTrigger)

    let dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Discard unsaved changes?')

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByLabelText('Title')).toHaveValue('Weekend raid')
    expect(navigateSpy).not.toHaveBeenCalled()

    fireEvent.click(cancelTrigger)
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard' }))
    expect(navigateSpy).toHaveBeenCalledWith('/polls')
  })

  it('cancels straight through with no discard dialog when the form is clean_D162', async () => {
    mockGet()
    renderPage()

    await screen.findByLabelText('Title')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(navigateSpy).toHaveBeenCalledWith('/polls')
  })

  describe('prefill poll arm (S7, D-102/D-163, A-083)', () => {
    it('reads repeated startTimes into the option editor, deduped and sorted_D102', async () => {
      mockGet()
      renderPage(
        '/polls/new?startTimes=2099-03-02T10%3A00%3A00Z&startTimes=2099-03-01T10%3A00%3A00Z&startTimes=2099-03-01T10%3A00%3A00Z',
      )

      const list = await screen.findByRole('group', { name: 'Candidate times' })
      const items = within(list).getAllByRole('listitem')
      // Deduped (the repeated instant collapses to one) and sorted, never the URL's own order.
      expect(items).toHaveLength(2)
      expect(items[0]).toHaveTextContent('Mar 1, 2099')
      expect(items[1]).toHaveTextContent('Mar 2, 2099')
    })

    it('clamps a past-lead startTime up to now + 15 min and surfaces the clamp, never silently_D102', async () => {
      mockGet()
      renderPage(`/polls/new?startTimes=${encodeURIComponent(new Date(0).toISOString())}`)

      expect(
        await screen.findByText(
          'The start time was moved to the earliest time allowed (15 minutes from now).',
        ),
      ).toBeInTheDocument()
      const list = screen.getByRole('group', { name: 'Candidate times' })
      expect(within(list).getAllByRole('listitem')).toHaveLength(1)
    })

    it('truncates a prefill past the 12-option cap and surfaces it, never silently_F4b', async () => {
      mockGet()
      const starts = Array.from({ length: 14 }, (_, index) =>
        encodeURIComponent(`2099-04-${String(index + 1).padStart(2, '0')}T10:00:00Z`),
      )
      renderPage(`/polls/new?${starts.map((s) => `startTimes=${s}`).join('&')}`)

      const list = await screen.findByRole('group', { name: 'Candidate times' })
      expect(within(list).getAllByRole('listitem')).toHaveLength(12)
      expect(
        await screen.findByText(
          'Only the first 12 candidate times from your selection were kept — a poll can have at most 12.',
        ),
      ).toBeInTheDocument()
    })

    it('never surfaces a truncation notice with fewer prefilled instants than the cap', async () => {
      mockGet()
      renderPage('/polls/new?startTimes=2099-04-01T10%3A00%3A00Z&startTimes=2099-04-02T10%3A00%3A00Z')

      await screen.findByRole('group', { name: 'Candidate times' })
      expect(
        screen.queryByText(/from your selection were kept/),
      ).not.toBeInTheDocument()
    })

    it('never surfaces a clamp notice with no prefill at all', async () => {
      mockGet()
      renderPage()

      await screen.findByLabelText('Title')
      expect(
        screen.queryByText(
          'The start time was moved to the earliest time allowed (15 minutes from now).',
        ),
      ).not.toBeInTheDocument()
    })
  })

  it('disables free cells in the shown week once the cap is reached across weeks, not just the visible week_F5', async () => {
    mockGet()
    // Pinned to the exact start of a week so every candidate placed later in
    // that week (or the next) is unambiguously in the future, regardless of
    // which real-world weekday the suite runs on.
    vi.useFakeTimers({ toFake: ['Date'] })
    try {
      const anchorNow = instantToFakeLocal(new Date('2099-04-01T12:00:00Z').toISOString(), 'UTC')
      const weekStart = startOfLocaleWeek(anchorNow, weekStartsOnIso('en'))
      vi.setSystemTime(fakeLocalToInstant(weekStart, 'UTC'))

      // 6 candidates land in the displayed week (days 0-5), 6 more land a
      // week later — 12 total, the cap, but only 6 are ever in view at once.
      const inWeek = Array.from({ length: 6 }, (_, i) =>
        fakeLocalToInstant(new Date(weekStart.getTime() + i * DAY_MS + 23 * 60 * 60_000), 'UTC'),
      )
      const nextWeek = Array.from({ length: 6 }, (_, i) =>
        fakeLocalToInstant(
          new Date(weekStart.getTime() + (7 + i) * DAY_MS + 23 * 60 * 60_000),
          'UTC',
        ),
      )
      const query = [...inWeek, ...nextWeek]
        .map((iso) => `startTimes=${encodeURIComponent(iso)}`)
        .join('&')
      renderPage(`/polls/new?${query}`)

      const list = await screen.findByRole('group', { name: 'Candidate times' })
      expect(within(list).getAllByRole('listitem')).toHaveLength(12)

      const grid = await screen.findByTestId('candidate-grid')
      await within(grid).findAllByRole('gridcell')
      // Day 6 (20:00) carries no candidate in the displayed week — if the cap
      // were checked only against the in-view 6, this cell would stay enabled.
      const freeCell = within(grid).getByTestId('candidate-slot-6-1200')
      expect(freeCell).toHaveAttribute('aria-disabled', 'true')
    } finally {
      vi.useRealTimers()
    }
  })

  it('renders real friend-availability windows on the grid as chips, masked owner drawn anonymous_F6', async () => {
    mockGet({
      calendarView: {
        ...emptyCalendarView,
        friendAvailability: [
          {
            owner: {
              ordinal: 1,
              displayName: 'Robin',
              avatar: { kind: 'emoji', emoji: '🎯', avatarSourceId: null, imgUrl: null },
            },
            // Spans far wider than any displayed week, so it always overlaps
            // whichever week the grid renders (no dependency on "now").
            ranges: [{ start: '2000-01-01T00:00:00Z', end: '2100-01-01T00:00:00Z' }],
          },
          {
            // D-076: a masked owner arrives with both `displayName` and
            // `avatar` withheld by the server.
            owner: { ordinal: 2, displayName: null, avatar: null },
            ranges: [{ start: '2000-01-01T00:00:00Z', end: '2100-01-01T00:00:00Z' }],
          },
        ],
      },
    })
    renderPage()

    const grid = await screen.findByTestId('candidate-grid')
    // The window spans the whole displayed week, so every cell reports the
    // same count — any match proves the two owners reached the grid.
    expect(await within(grid).findAllByRole('gridcell', { name: /2 friends free/ })).not.toHaveLength(
      0,
    )

    // …and both owners are painted as chips on the friend layer, the masked
    // one drawn as the neutral anonymous disc rather than an invented glyph.
    const chips = await within(grid).findAllByText(/· \d\d:\d\d–/)
    const discs = chips.map(
      (chip) => chip.parentElement!.querySelector('[aria-hidden="true"]') as HTMLElement,
    )
    const maskedDisc = discs.find((disc) => disc.className.includes('bg-busy'))
    const realDisc = discs.find((disc) => disc.textContent === '🎯')
    expect(maskedDisc?.textContent).toBe('●')
    expect(realDisc).toBeTruthy()
  })

  it('edits to the duration field reflow every candidate band_D131', async () => {
    mockGet()
    renderPage()

    const slots = await findGridSlots()
    fireEvent.click(slots[0])

    const grid = screen.getByTestId('candidate-grid')
    const [band] = within(grid).getAllByRole('button')
    expect(band).toHaveTextContent('16:00–18:00')

    fireEvent.change(screen.getByLabelText('Duration (minutes)'), { target: { value: '90' } })

    expect(within(grid).getAllByRole('button')[0]).toHaveTextContent('16:00–17:30')
  })

  it('a band resize writes the new shared duration back into the field_D131', async () => {
    mockGet()
    renderPage()

    const slots = await findGridSlots()
    fireEvent.click(slots[0])

    const grid = screen.getByTestId('candidate-grid')
    // Shift+Arrow is the resize handle's keyboard equivalent: it changes the
    // poll's one shared duration, in D-131's 15-minute increments.
    fireEvent.keyDown(within(grid).getAllByRole('button')[0], {
      key: 'ArrowDown',
      shiftKey: true,
    })

    expect(screen.getByLabelText('Duration (minutes)')).toHaveValue(135)
    expect(within(grid).getAllByRole('button')[0]).toHaveTextContent('16:00–18:15')
  })

  it('paints the calendar response scheduled sessions underneath the candidate layer', async () => {
    const zonedNow = instantToFakeLocal(new Date().toISOString(), 'UTC')
    const weekStart = startOfLocaleWeek(zonedNow, weekStartsOnIso('en'))
    const startTime = fakeLocalToInstant(
      new Date(weekStart.getTime() + 2 * DAY_MS + 18 * 60 * 60_000),
      'UTC',
    )
    mockGet({
      calendarView: {
        ...emptyCalendarView,
        sessions: [
          {
            id: 'session-1',
            title: 'Deep Rock night',
            startTime,
            durationMinutes: 120,
            activeOccupancy: 3,
          },
        ],
      },
    })
    renderPage()

    const pill = await screen.findByTestId('candidate-session-session-1')
    expect(pill).toHaveTextContent('Deep Rock night')
    expect(pill).toHaveTextContent('3 in')
  })

  it('formats option instants in the account language, not the browser locale_D146', async () => {
    mockGet()
    await i18n.changeLanguage('pt-BR')
    try {
      renderPage()

      const slots = await findGridSlots()
      fireEvent.click(slots[0])

      const list = await screen.findByRole('group', { name: 'Opções de horário' })
      const [item] = within(list).getAllByRole('listitem')

      // pt-BR's Intl format never matches the English one for the same instant.
      const enOnlyFormat = new Intl.DateTimeFormat('en', {
        dateStyle: 'medium',
        timeStyle: 'short',
      })
      expect(item?.textContent).not.toBe('')
      expect(screen.queryByText(enOnlyFormat.format(new Date(0)))).not.toBeInTheDocument()
    } finally {
      await i18n.changeLanguage('en')
    }
  })
})
