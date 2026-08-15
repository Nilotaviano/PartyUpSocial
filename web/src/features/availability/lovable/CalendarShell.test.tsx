import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
}))

vi.mock('../../../api/client', () => ({
  client: { GET: api.get, POST: api.post, PATCH: api.patch, DELETE: api.del },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

import '../../../i18n'
import { CalendarShell } from './CalendarShell'

// en-US puts the first day of the week on Sunday, so the shown week starts 09 Aug.
const WEEK_START = '2026-08-09T00:00:00.000Z'

function calendarResponse() {
  return {
    myAvailability: {
      occurrences: [
        {
          startTime: '2026-08-11T20:00:00Z',
          endTime: '2026-08-11T23:00:00Z',
          origin: {
            kind: 'block',
            blockId: '11111111-1111-1111-1111-111111111111',
            seriesId: null,
            occurrenceDate: null,
          },
        },
        {
          startTime: '2026-08-13T19:30:00Z',
          endTime: '2026-08-13T22:00:00Z',
          origin: {
            kind: 'occurrence',
            blockId: null,
            seriesId: '22222222-2222-2222-2222-222222222222',
            occurrenceDate: '2026-08-13',
          },
        },
      ],
      merged: [] as { start: string; end: string }[],
      range: null,
      series: [
        {
          seriesId: '22222222-2222-2222-2222-222222222222',
          dayOfWeek: 4,
          startTime: '19:30',
          durationMinutes: 150,
          zoneId: 'UTC',
          effectiveFrom: '2026-08-01',
          effectiveUntil: null,
        },
      ],
    },
    friendAvailability: [
      {
        owner: {
          ordinal: 1,
          displayName: 'Mika Ortega',
          avatar: { kind: 'emoji', emoji: '🦊', avatarSourceId: null, imgUrl: null },
        },
        ranges: [{ start: '2026-08-11T20:00:00Z', end: '2026-08-11T23:00:00Z' }],
      },
      // D-076: the server withholds the avatar wherever it withholds the name.
      { owner: { ordinal: 2, displayName: null }, ranges: [] },
    ],
    sessions: [
      {
        id: '33333333-3333-3333-3333-333333333333',
        organizerId: null,
        title: 'Helldivers 2',
        startTime: '2026-08-11T21:00:00Z',
        durationMinutes: 120,
        platform: 'pc',
        game: null,
        status: 'scheduled',
        isPrivate: false,
        maxCapacity: null,
        activeOccupancy: 4,
        organizer: { ordinal: 1, displayName: 'Mika Ortega' },
        viewerRelation: 'participant',
      },
    ],
    pollOptions: [
      {
        pollId: '44444444-4444-4444-4444-444444444444',
        title: 'Deep Rock co-op',
        options: [
          {
            optionId: '55555555-5555-5555-5555-555555555555',
            startTime: '2026-08-13T20:00:00Z',
            ownVote: 'available',
          },
        ],
      },
    ],
  }
}

function renderShell(route = '/calendar') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[route]}>
        <CalendarShell />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

/** Focus a grid cell the way a keyboard viewer reaches one. */
function focusCell(name: string) {
  const cell = screen.getByRole('gridcell', { name })
  act(() => cell.focus())
  return cell
}

/** Enter, Shift+ArrowDown, Enter — the whole keyboard paint (P5-CAL-01). */
function paintFromKeyboard(name: string) {
  const cell = focusCell(name)
  fireEvent.keyDown(cell, { key: 'Enter' })
  fireEvent.keyDown(cell, { key: 'ArrowDown', shiftKey: true })
  fireEvent.keyDown(document.activeElement ?? cell, { key: 'Enter' })
}

/** The FriendsCard pill list: the Friends heading's sibling <ul>, not UpNext's list. */
function crewList() {
  const heading = screen.getAllByRole('heading', { name: /^friends$/i })[0]
  return heading.nextElementSibling as HTMLElement
}

async function waitForCalendar() {
  await screen.findByTestId('screen-calendar')
  await waitFor(() => expect(screen.getAllByRole('gridcell').length).toBeGreaterThan(0))
}

describe('CalendarShell', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    vi.setSystemTime(new Date('2026-08-13T18:00:00Z'))
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: { timeZone: 'UTC', locale: 'en' } })
      }
      if (path === '/api/v1/calendar') {
        return Promise.resolve({ data: calendarResponse() })
      }
      return Promise.resolve({ data: undefined })
    })
  })

  afterEach(() => {
    cleanup()
    vi.useRealTimers()
    vi.clearAllMocks()
  })

  it('renders the calendar shell from the live calendar response', async () => {
    renderShell()

    expect(await screen.findByTestId('screen-calendar')).toBeInTheDocument()
    // Week-of subtitle is derived from the shown week, not a demo constant.
    expect(await screen.findByText(/Week of Aug 9/)).toBeInTheDocument()
    // Session and friend data both come from the one /calendar response.
    expect(await screen.findAllByText(/Helldivers 2/)).not.toHaveLength(0)
    await waitFor(() =>
      expect(screen.getAllByText('Mika').length).toBeGreaterThan(0),
    )
  })

  it('exposes the 15-minute slots as an ARIA grid for keyboard users', async () => {
    renderShell()

    const grid = await screen.findByRole('grid')
    expect(grid).toBeInTheDocument()
    const cells = screen.getAllByRole('gridcell')
    // 08:00–24:00 at 15-minute rows across seven days.
    expect(cells).toHaveLength((16 * 60) / 15 * 7)
    expect(cells.filter((cell) => cell.getAttribute('tabindex') === '0')).toHaveLength(1)
  })

  it('creates nothing until the commit dialog is confirmed_D009', async () => {
    renderShell()
    await waitForCalendar()

    paintFromKeyboard('Mon 09:00')

    // The paint is a proposal: the dialog is up and no write has left yet.
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('09:00–09:30')).toBeInTheDocument()
    expect(api.post).not.toHaveBeenCalled()

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(api.post).not.toHaveBeenCalled()
  })

  it('saves a one-time block by default and a weekly series on request_D132', async () => {
    api.post.mockResolvedValue({ data: { blockId: 'new-block', seriesId: 'new-series' } })
    renderShell()
    await waitForCalendar()

    paintFromKeyboard('Mon 09:00')
    let dialog = await screen.findByRole('dialog')
    // "One-time" is pressed before anything is touched (D-009's safe default).
    expect(within(dialog).getByRole('button', { name: 'One-time' })).toHaveAttribute(
      'aria-pressed',
      'true',
    )
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/me/availability/blocks',
        expect.anything(),
      ),
    )
    const blockBody = api.post.mock.calls.at(-1)![1].body
    expect(blockBody.startTime).toBe('2026-08-10T09:00:00.000Z')
    expect(blockBody.endTime).toBe('2026-08-10T09:30:00.000Z')

    paintFromKeyboard('Tue 11:00')
    dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Repeats weekly' }))
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() =>
      expect(api.post).toHaveBeenCalledWith(
        '/api/v1/me/availability/series',
        expect.anything(),
      ),
    )
    const seriesBody = api.post.mock.calls.at(-1)![1].body
    // ISO weekday, "HH:mm" with no seconds, and the painted length.
    expect(seriesBody).toMatchObject({
      dayOfWeek: 2,
      startTime: '11:00',
      durationMinutes: 30,
      effectiveFrom: '2026-08-11',
      effectiveUntil: null,
    })
  })

  it('keeps Tab inside the modal commit dialog_D139', async () => {
    renderShell()
    await waitForCalendar()

    paintFromKeyboard('Mon 09:00')
    const dialog = await screen.findByRole('dialog')
    const buttons = within(dialog).getAllByRole('button')
    // Focus opens on the first control, so a keyboard viewer starts inside.
    expect(document.activeElement).toBe(buttons[0])

    act(() => buttons[buttons.length - 1].focus())
    fireEvent.keyDown(dialog, { key: 'Tab' })
    expect(document.activeElement).toBe(buttons[0])

    fireEvent.keyDown(dialog, { key: 'Tab', shiftKey: true })
    expect(document.activeElement).toBe(buttons[buttons.length - 1])
  })

  it('returns focus to the grid when the commit dialog closes_D132', async () => {
    renderShell()
    await waitForCalendar()

    paintFromKeyboard('Mon 09:00')
    const dialog = await screen.findByRole('dialog')
    fireEvent.keyDown(dialog, { key: 'Escape' })

    // Focus lands back on the grid's roving tab stop rather than the body.
    await waitFor(() => {
      const active = document.activeElement
      expect(active).not.toBe(document.body)
      expect(active?.getAttribute('role')).toBe('gridcell')
      expect(active?.getAttribute('tabindex')).toBe('0')
    })
  })

  it('asks which occurrences a weekly edit covers, defaulting to this one_D134_D143', async () => {
    api.patch.mockResolvedValue({ data: {} })
    renderShell()
    await waitForCalendar()

    // The Thursday 19:30 rule comes from a series, so editing it is scoped.
    fireEvent.click(screen.getAllByRole('button', { name: 'Edit window' })[0])
    fireEvent.change(screen.getByRole('combobox', { name: 'Start time' }), {
      target: { value: String(19 * 60) },
    })

    const scope = await screen.findByRole('dialog')
    expect(
      within(scope).getByRole('heading', { name: 'Which occurrences?' }),
    ).toBeInTheDocument()
    expect(within(scope).getByRole('radio', { name: 'Only this occurrence' })).toBeChecked()
    expect(
      within(scope).getByRole('radio', { name: 'This and future occurrences' }),
    ).not.toBeChecked()
    // Nothing is written while the question is still open.
    expect(api.patch).not.toHaveBeenCalled()

    fireEvent.click(within(scope).getByRole('button', { name: 'Confirm' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalled())
    const [path, options] = api.patch.mock.calls.at(-1)!
    expect(path).toBe('/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}')
    expect(options.params.path).toEqual({
      seriesId: '22222222-2222-2222-2222-222222222222',
      occurrenceDate: '2026-08-13',
    })
    expect(options.body).toMatchObject({ scope: 'onlyThis', dayOfWeek: null })
  })

  it('saves the thisAndFuture scope as the series shape, not two instants_D143', async () => {
    api.patch.mockResolvedValue({ data: {} })
    renderShell()
    await waitForCalendar()

    fireEvent.click(screen.getAllByRole('button', { name: 'Edit window' })[0])
    fireEvent.change(screen.getByRole('combobox', { name: 'Start time' }), {
      target: { value: String(19 * 60) },
    })
    const scope = await screen.findByRole('dialog')
    fireEvent.click(within(scope).getByRole('radio', { name: 'This and future occurrences' }))
    fireEvent.click(within(scope).getByRole('button', { name: 'Confirm' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalled())
    expect(api.patch.mock.calls.at(-1)![1].body).toEqual({
      scope: 'thisAndFuture',
      // A local "HH:mm" wall time, not the instant the onlyThis arm sends, and no endTime at
      // all — the endpoint rejects a thisAndFuture body carrying either shape.
      startTime: '19:00',
      endTime: null,
      dayOfWeek: 4,
      durationMinutes: 180,
    })
  })

  it('edits a one-time block straight through, with no scope question_D025', async () => {
    api.patch.mockResolvedValue({ data: {} })
    renderShell()
    await waitForCalendar()

    // The second rule row is the one-time block (20:00–23:00 on Tuesday).
    fireEvent.click(screen.getAllByRole('button', { name: 'Edit window' })[1])
    fireEvent.change(screen.getAllByRole('combobox', { name: 'Start time' })[0], {
      target: { value: String(19 * 60) },
    })

    await waitFor(() => expect(api.patch).toHaveBeenCalled())
    expect(api.patch.mock.calls.at(-1)![0]).toBe('/api/v1/me/availability/blocks/{blockId}')
    expect(screen.queryByText('Which occurrences?')).not.toBeInTheDocument()
  })

  it('deletes a weekly rule only after the confirm, and the series with it_D025', async () => {
    api.del.mockResolvedValue({ data: undefined })
    renderShell()
    await waitForCalendar()

    fireEvent.click(screen.getAllByRole('button', { name: 'Delete window' })[0])
    expect(api.del).not.toHaveBeenCalled()

    const confirm = await screen.findByRole('alertdialog')
    fireEvent.click(within(confirm).getByRole('button', { name: 'Delete rule' }))

    await waitFor(() => expect(api.del).toHaveBeenCalled())
    const [path, options] = api.del.mock.calls.at(-1)!
    expect(path).toBe('/api/v1/me/availability/series/{seriesId}')
    expect(options.params.path.seriesId).toBe('22222222-2222-2222-2222-222222222222')
  })

  it('paints busy cells from the server merge rather than the occurrences_D101', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: { timeZone: 'UTC', locale: 'en' } })
      }
      if (path === '/api/v1/calendar') {
        const body = calendarResponse()
        // The occurrence runs 20:00–23:00; the server's merge deliberately
        // stops at 21:00. A client re-deriving the union (A-076 forbids it)
        // would paint 21:00 busy too.
        body.myAvailability.merged = [
          { start: '2026-08-11T20:00:00Z', end: '2026-08-11T21:00:00Z' },
        ]
        return Promise.resolve({ data: body })
      }
      return Promise.resolve({ data: undefined })
    })
    renderShell()
    await waitForCalendar()

    await waitFor(() =>
      expect(screen.getByRole('gridcell', { name: 'Tue 20:00 – Busy' })).toHaveAttribute(
        'data-state',
        'busy',
      ),
    )
    expect(screen.getByRole('gridcell', { name: 'Tue 21:00' })).toHaveAttribute(
      'data-state',
      'free',
    )
  })

  it('renders every calendar layer from the one response_D111', async () => {
    renderShell()
    await waitForCalendar()

    // Own availability, a friend, a booked session and an open poll option —
    // each named in words, none carried by colour alone (WCAG 1.4.1).
    expect(await screen.findByText('Thu · 19:30–22:00')).toBeInTheDocument()
    expect(screen.getByTitle(/Mika Ortega/)).toBeInTheDocument()
    expect(screen.getAllByText(/Helldivers 2/).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/Deep Rock co-op/).length).toBeGreaterThan(0)
    expect(screen.getAllByText(/your vote: available/).length).toBeGreaterThan(0)

    // One fetch carried all of it.
    expect(api.get.mock.calls.filter((c) => c[0] === '/api/v1/calendar')).toHaveLength(1)
  })

  it('renders an avatar disc per crew member, decoration only_D188', async () => {
    renderShell()
    await waitForCalendar()

    const crew = crewList()
    const named = within(crew).getByText('Mika').closest('li')!
    // The disc is aria-hidden decoration beside the name, never the pill's label.
    expect(named).toHaveTextContent('🦊')
    expect(named.querySelector('[aria-hidden="true"]')).toHaveTextContent('🦊')
    expect(within(crew).queryByRole('img')).toBeNull()
  })

  it('renders the masked crew member as a neutral disc that leaks no emoji_D188_D076', async () => {
    renderShell()
    await waitForCalendar()

    const crew = crewList()
    const masked = within(crew).getByText('Anonymous').closest('li')!
    // The server sends no avatar for a masked owner, and the client invents none:
    // the neutral '●' placeholder, never the named friend's glyph.
    expect(masked).toHaveTextContent('●')
    expect(masked).not.toHaveTextContent('🦊')
    expect(masked.querySelector('img')).toBeNull()
    // The busy fill is reachable only through AvatarDisc's `masked` arm, so this
    // pins the masking decision rather than an incidentally empty avatar.
    expect(masked.querySelector('[aria-hidden="true"]')).toHaveClass('bg-busy')
  })

  it('renders the agenda over the same range as the week grid, from the same fetch_D104', async () => {
    renderShell('/calendar/agenda')
    await screen.findByTestId('screen-calendar')

    const agenda = await screen.findByRole('list', { name: 'Agenda' })
    const rows = within(agenda).getAllByRole('listitem')
    // Two availability occurrences, one session and one poll option — the same
    // week the grid would paint, sorted chronologically.
    expect(rows).toHaveLength(4)
    // Chronological across every layer, not grouped by kind.
    expect(rows[0]).toHaveTextContent('My availability')
    expect(rows[1]).toHaveTextContent('Helldivers 2')
    expect(rows.at(-1)).toHaveTextContent('Deep Rock co-op')
    expect(screen.queryByRole('grid')).not.toBeInTheDocument()
    expect(api.get.mock.calls.filter((c) => c[0] === '/api/v1/calendar')).toHaveLength(1)

    // Both views stay reachable from the calendar itself, not by URL alone.
    expect(screen.getByRole('tab', { name: 'Agenda' })).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByRole('tab', { name: 'Week' })).toHaveAttribute('aria-selected', 'false')
  })

  it('reads today from the viewer profile timezone, not the host clock_D046', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        // 2026-08-13T18:00Z is already Friday the 14th in Tokyo.
        return Promise.resolve({ data: { timeZone: 'Asia/Tokyo', locale: 'en' } })
      }
      if (path === '/api/v1/calendar') return Promise.resolve({ data: calendarResponse() })
      return Promise.resolve({ data: undefined })
    })
    renderShell()
    await waitForCalendar()

    await waitFor(() => {
      const today = screen
        .getAllByRole('button')
        .filter((b) => b.getAttribute('aria-current') === 'date')
      expect(today).toHaveLength(1)
      expect(today[0]).toHaveTextContent('Fri')
    })
  })

  it('reveals the earlier hours and says so out loud_S4', async () => {
    renderShell()
    await waitForCalendar()

    expect(screen.queryByRole('gridcell', { name: 'Mon 02:00' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Show 00:00–08:00' }))

    expect(screen.getByRole('gridcell', { name: 'Mon 02:00' })).toBeInTheDocument()
    expect(screen.getByTestId('early-hours-status')).toHaveTextContent(
      'Earlier hours shown. The grid now starts at midnight.',
    )
    expect(screen.getByRole('button', { name: 'Hide 00:00–08:00' })).toBeInTheDocument()
  })

  it('requests the week that contains the mount instant', async () => {
    renderShell()

    await waitFor(() =>
      expect(
        api.get.mock.calls.some(
          (call) =>
            call[0] === '/api/v1/calendar' &&
            (call[1] as { params: { query: { from: string } } }).params.query.from ===
              WEEK_START,
        ),
      ).toBe(true),
    )
  })

  it('shows every friend chip by default and toggling a pill off removes it from the grid_D117', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: { timeZone: 'UTC', locale: 'en' } })
      }
      if (path === '/api/v1/calendar') {
        const body = calendarResponse()
        // Gives the masked friend a window too, so both fixture friends paint a
        // grid chip and a toggle on one can be proven to leave the other alone.
        body.friendAvailability[1]!.ranges = [
          { start: '2026-08-11T14:00:00Z', end: '2026-08-11T15:00:00Z' },
        ]
        return Promise.resolve({ data: body })
      }
      return Promise.resolve({ data: undefined })
    })
    renderShell()
    await waitForCalendar()

    const crew = crewList()
    expect(screen.getByTitle(/Mika Ortega/)).toBeInTheDocument()
    expect(screen.getByTitle(/Anonymous/)).toBeInTheDocument()
    const toggle = within(crew).getByRole('button', {
      name: 'Toggle availability for Mika Ortega',
    })
    expect(toggle).toHaveAttribute('aria-pressed', 'true')

    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-pressed', 'false')
    expect(screen.queryByTitle(/Mika Ortega/)).not.toBeInTheDocument()
    // The other friend's chip is untouched by this toggle.
    expect(screen.getByTitle(/Anonymous/)).toBeInTheDocument()
    // The legend pill stays visible (dimmed), never removed — it is the truthful filter control.
    expect(within(crew).getByText('Mika')).toBeInTheDocument()

    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTitle(/Mika Ortega/)).toBeInTheDocument()
  })

  it('resets the friend filter on week navigation, since a friend id is only stable within one response', async () => {
    renderShell()
    await waitForCalendar()

    const toggle = () =>
      within(crewList()).getByRole('button', { name: 'Toggle availability for Mika Ortega' })
    fireEvent.click(toggle())
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')

    // Every step below actually moves the fetched `from` — the single reset
    // mechanism (a render-phase reset keyed on `from`) fires on each, never on
    // a same-week no-op, so the sequence never revisits an unchanged range.
    fireEvent.click(screen.getByRole('button', { name: 'Next week' }))
    await waitFor(() => expect(toggle()).toHaveAttribute('aria-pressed', 'true'))

    fireEvent.click(toggle())
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')

    // offset 1 -> 0: still a genuine range change.
    fireEvent.click(screen.getByRole('button', { name: 'Today' }))
    await waitFor(() => expect(toggle()).toHaveAttribute('aria-pressed', 'true'))

    fireEvent.click(toggle())
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')

    // offset 0 -> -1: also a genuine range change.
    fireEvent.click(screen.getByRole('button', { name: 'Previous week' }))
    await waitFor(() => expect(toggle()).toHaveAttribute('aria-pressed', 'true'))
  })

  it('codes every calendar layer by text, never by its colour swatch alone_D139', async () => {
    renderShell()
    await waitForCalendar()

    // Every legend row names its layer in words; the coloured swatch beside it
    // is `aria-hidden` decoration, never the sole cue (WCAG 1.4.1).
    for (const label of [
      'Free (you)',
      'Maybe (you)',
      'Friend free',
      'Booked session',
      'Poll option',
    ]) {
      const item = screen.getByText(label).closest('li')!
      expect(item.querySelector('[aria-hidden="true"]')).toBeInTheDocument()
    }

    // Each crew pill carries the friend's own name as text, not colour or a glyph alone.
    const crew = crewList()
    expect(within(crew).getByText('Mika')).toBeInTheDocument()
    expect(within(crew).getByText('Anonymous')).toBeInTheDocument()
  })

  it('resets the friend filter to all-shown on remount, with no persisted write_D135', async () => {
    const localStorageSpy = vi.spyOn(Storage.prototype, 'setItem')
    const historySpy = vi.spyOn(window.history, 'pushState')

    const first = renderShell()
    await waitForCalendar()

    const toggle = () =>
      within(crewList()).getByRole('button', { name: 'Toggle availability for Mika Ortega' })
    fireEvent.click(toggle())
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')
    expect(screen.queryByTitle(/Mika Ortega/)).not.toBeInTheDocument()

    first.unmount()

    renderShell()
    await waitForCalendar()

    expect(toggle()).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByTitle(/Mika Ortega/)).toBeInTheDocument()
    expect(localStorageSpy).not.toHaveBeenCalled()
    expect(historySpy).not.toHaveBeenCalled()
  })
})

/**
 * The day pager is the phone's only week navigation — the header's week arrows
 * scroll off screen there — so these run against a mobile viewport, which is
 * what narrows the grid to a three-day window in the first place.
 */
describe('CalendarShell on a phone', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    vi.setSystemTime(new Date('2026-08-13T18:00:00Z'))
    vi.stubGlobal('innerWidth', 430)
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: true,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    }))
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: { timeZone: 'UTC', locale: 'en' } })
      }
      if (path === '/api/v1/calendar') {
        return Promise.resolve({ data: calendarResponse() })
      }
      return Promise.resolve({ data: undefined })
    })
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
    vi.useRealTimers()
    vi.clearAllMocks()
  })

  const pagerLabel = () =>
    within(screen.getByTestId('day-pager')).getByText(/·\s*swipe the strip/)

  it('rolls the day pager into the next week past the last window_P5MOB01', async () => {
    renderShell()
    await waitForCalendar()

    // Today is Thu 13 Aug, so the window opens on Wed–Fri.
    expect(pagerLabel()).toHaveTextContent('Wed – Fri')
    const later = screen.getByRole('button', { name: 'Later days' })

    fireEvent.click(later)
    expect(pagerLabel()).toHaveTextContent('Thu – Sat')

    // The window is already at the end of the week: the next step must reach
    // the following week rather than sticking.
    fireEvent.click(later)
    expect(pagerLabel()).toHaveTextContent('Sun – Tue')
    expect(await screen.findByText(/Week of Aug 16/)).toBeInTheDocument()
  })

  it('rolls the day pager back into the previous week before the first window_P5MOB01', async () => {
    renderShell()
    await waitForCalendar()

    const earlier = screen.getByRole('button', { name: 'Earlier days' })
    for (const expected of ['Tue – Thu', 'Mon – Wed', 'Sun – Tue']) {
      fireEvent.click(earlier)
      expect(pagerLabel()).toHaveTextContent(expected)
    }
    expect(screen.getByText(/Week of Aug 9/)).toBeInTheDocument()

    fireEvent.click(earlier)
    expect(pagerLabel()).toHaveTextContent('Thu – Sat')
    expect(await screen.findByText(/Week of Aug 2/)).toBeInTheDocument()
  })

  it('keeps the friend filter reset when a rollover changes the fetched week_D117', async () => {
    renderShell()
    await waitForCalendar()

    const toggle = () =>
      within(crewList()).getByRole('button', { name: 'Toggle availability for Mika Ortega' })
    fireEvent.click(toggle())
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')

    const later = screen.getByRole('button', { name: 'Later days' })
    fireEvent.click(later)
    // Same week so far — the filter is untouched by plain paging.
    expect(toggle()).toHaveAttribute('aria-pressed', 'false')

    fireEvent.click(later)
    await waitFor(() => expect(toggle()).toHaveAttribute('aria-pressed', 'true'))
  })

  it('shows the long-press paint hint on a phone_P5MOB02', async () => {
    renderShell()
    await waitForCalendar()

    expect(screen.getByTestId('paint-hint-touch')).toHaveTextContent(
      'Press and hold a day to add availability',
    )
  })
})
