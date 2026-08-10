import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const api = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  patch: vi.fn(),
  del: vi.fn(),
}))

vi.mock('../../api/client', () => ({
  client: { GET: api.get, POST: api.post, PATCH: api.patch, DELETE: api.del },
  isApiError: (error: unknown) =>
    Boolean(error && typeof error === 'object' && 'code' in error),
}))

import '../../i18n'
import {
  WeekGrid,
  type GridFriendRange,
  type GridOccurrence,
  type GridPollOptionEntry,
  type GridRange,
  type GridSessionEntry,
} from './WeekGrid'
import { WeekGridPage } from './WeekGridPage'

// Fake-local convention (matches WeekGrid/WeekGridPage): the UTC accessors
// hold the wall-clock components, independent of the host machine's own
// timezone.
function localDate(hour: number, minute = 0, day = 10) {
  return new Date(Date.UTC(2026, 7, day, hour, minute, 0, 0))
}

function timeLabelFormatter(date: Date) {
  return `${date.getUTCHours()}:${String(date.getUTCMinutes()).padStart(2, '0')}`
}

const dayLabels = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

function renderGrid(props: {
  occurrences?: GridOccurrence[]
  merged?: GridRange[]
  friendRanges?: GridFriendRange[]
  sessionEntries?: GridSessionEntry[]
  pollOptionEntries?: GridPollOptionEntry[]
  onCreateBlock?: ReturnType<typeof vi.fn<(range: GridRange) => void>>
  onEditOccurrence?: ReturnType<typeof vi.fn<(id: string) => void>>
  startHour?: number
  endHour?: number
}) {
  const onCreateBlock = props.onCreateBlock ?? vi.fn<(range: GridRange) => void>()
  const onEditOccurrence = props.onEditOccurrence ?? vi.fn<(id: string) => void>()
  render(
    <WeekGrid
      dayLabels={dayLabels}
      endHour={props.endHour ?? 11}
      friendRanges={props.friendRanges ?? []}
      merged={props.merged ?? []}
      occurrences={props.occurrences ?? []}
      pollOptionEntries={props.pollOptionEntries ?? []}
      sessionEntries={props.sessionEntries ?? []}
      startHour={props.startHour ?? 9}
      timeLabelFormatter={timeLabelFormatter}
      weekStart={localDate(0)}
      onCreateBlock={onCreateBlock}
      onEditOccurrence={onEditOccurrence}
    />,
  )
  return { onCreateBlock, onEditOccurrence }
}

afterEach(() => {
  cleanup()
  vi.clearAllMocks()
})

describe('WeekGrid keyboard flow', () => {
  it('creates extends commits and cancels a selection with the keyboard alone_D071_D132', () => {
    const { onCreateBlock } = renderGrid({})

    const start = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')

    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    expect(screen.getByRole('status')).toHaveTextContent('Selected 9:00 to 9:30.')

    const extended = screen.getAllByRole('gridcell', { name: '9:15' })[0]
    fireEvent.keyDown(extended, { key: 'Enter' })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [committed] = onCreateBlock.mock.calls[0]
    expect(committed.start.getTime()).toBe(localDate(9, 0).getTime())
    expect(committed.end.getTime()).toBe(localDate(9, 30).getTime())
    expect(screen.getByRole('status')).toHaveTextContent(
      'Availability created from 9:00 to 9:30.',
    )

    const another = screen.getAllByRole('gridcell', { name: '10:00' })[0]
    another.focus()
    fireEvent.keyDown(another, { key: 'Enter' })
    fireEvent.keyDown(another, { key: 'Escape' })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('status')).toHaveTextContent('Selection canceled.')
  })

  it('snaps both entry modes to fifteen minutes_D071', () => {
    const { onCreateBlock } = renderGrid({})

    const keyboardCell = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    keyboardCell.focus()
    fireEvent.keyDown(keyboardCell, { key: 'Enter' })
    fireEvent.keyDown(keyboardCell, { key: 'ArrowDown', shiftKey: true })
    fireEvent.keyDown(keyboardCell, { key: 'Enter' })

    const [keyboardRange] = onCreateBlock.mock.calls[0]
    expect(keyboardRange.start.getUTCMinutes() % 15).toBe(0)
    expect(keyboardRange.end.getUTCMinutes() % 15).toBe(0)
    expect((keyboardRange.end.getTime() - keyboardRange.start.getTime()) % (15 * 60_000)).toBe(0)

    onCreateBlock.mockClear()

    const dragStart = screen.getAllByRole('gridcell', { name: '10:00' })[0]
    const dragEnd = screen.getAllByRole('gridcell', { name: '10:30' })[0]
    fireEvent.mouseDown(dragStart, { button: 0 })
    fireEvent.mouseEnter(dragEnd)
    fireEvent.mouseUp(screen.getByRole('grid'))

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [dragRange] = onCreateBlock.mock.calls[0]
    expect(dragRange.start.getUTCMinutes() % 15).toBe(0)
    expect(dragRange.end.getUTCMinutes() % 15).toBe(0)
    expect(dragRange.start.getTime()).toBe(localDate(10, 0).getTime())
    expect(dragRange.end.getTime()).toBe(localDate(10, 45).getTime())
  })

  it('merges overlapping own blocks visually_D101', () => {
    const occurrences: GridOccurrence[] = [
      { id: 'block-a', start: localDate(9, 0), end: localDate(9, 30) },
      { id: 'block-b', start: localDate(9, 15), end: localDate(9, 45) },
    ]
    // The true union of these two occurrences IS 9:00–9:45, so a fixture
    // using that union can't discriminate the client trusting `merged`
    // verbatim (A-076) from a client silently re-deriving the same merge
    // itself — both would paint identically. `merged` is deliberately
    // short of block-b's own tail here (an
    // internal detail the client is never supposed to reconstruct, e.g. a
    // trailing occurrence the merge engine coalesced differently); the
    // assertions below check the painted band follows this exact —
    // "wrong-looking" — value, not what re-deriving from `occurrences`
    // would produce.
    const merged: GridRange[] = [{ start: localDate(9, 0), end: localDate(9, 30) }]
    const { onEditOccurrence } = renderGrid({ occurrences, merged })

    // Busy through 9:00–9:30 (`merged`), free at 9:30 even though block-b
    // (9:15–9:45) still legitimately covers that slot as an occurrence — a
    // client re-deriving the union from `occurrences` would paint 9:30 busy
    // too and fail this.
    for (const label of ['9:00', '9:15']) {
      expect(screen.getByRole('gridcell', { name: `${label} – Busy` })).toHaveAttribute(
        'data-state',
        'busy',
      )
    }
    const nineThirty = screen.getAllByRole('gridcell', { name: '9:30' })[0]
    expect(nineThirty).toHaveAttribute('data-state', 'free')

    // Both occurrences stay separately addressable for edit, including the
    // one `merged` doesn't fully paint.
    fireEvent.keyDown(screen.getByRole('gridcell', { name: '9:00 – Busy' }), { key: 'Enter' })
    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')

    fireEvent.keyDown(nineThirty, { key: 'Enter' })
    expect(onEditOccurrence).toHaveBeenCalledWith('block-b')
  })

  it('paints friendAvailability and sessions layers without adding gridcells or breaking roving tabindex_A080', () => {
    const friendRanges: GridFriendRange[] = [
      { ownerLabel: 'Ana', start: localDate(9, 0), end: localDate(9, 30) },
    ]
    const sessionEntries: GridSessionEntry[] = [
      {
        id: 'session-organizer',
        title: 'Raid night',
        start: localDate(9, 0),
        end: localDate(9, 30),
        viewerRelation: 'organizer',
      },
      {
        id: 'session-inherited',
        title: 'Open lobby',
        start: localDate(10, 0),
        end: localDate(10, 30),
        viewerRelation: 'inherited',
      },
    ]
    renderGrid({ friendRanges, sessionEntries })

    // Exactly 7 days × 8 slots (9:00–11:00 at 15-minute slots) — layers add
    // no gridcell of their own, so the roving-tabindex model (one tab stop)
    // is unchanged.
    const cells = screen.getAllByRole('gridcell')
    expect(cells).toHaveLength(56)
    expect(cells.filter((cell) => cell.getAttribute('tabindex') === '0')).toHaveLength(1)

    // Ana's range and the organizer's session both cover 9:00–9:30, so one
    // cell carries both layers' markers plus one accessible name naming both.
    const ownSessionCell = screen.getByRole('gridcell', { name: '9:00 – Ana available – Raid night' })
    expect(ownSessionCell.querySelector('[data-layer="friend"]')).not.toBeNull()
    const ownSessionLabel = ownSessionCell.querySelector('[data-layer="session"]')
    expect(ownSessionLabel).toHaveAttribute('data-viewer-relation', 'organizer')
    expect(ownSessionLabel).toHaveTextContent('Raid night')

    // The inherited/discoverable row is visibly distinct, and not by color
    // alone — a literal text label, not a class-only difference.
    const inheritedCell = screen.getByRole('gridcell', {
      name: '10:00 – Open lobby (discoverable)',
    })
    const inheritedLabel = inheritedCell.querySelector('[data-layer="session"]')
    expect(inheritedLabel).toHaveAttribute('data-viewer-relation', 'inherited')
    expect(inheritedLabel).toHaveTextContent('Open lobby (Discoverable)')
  })

  it('paints the pollOptions layer as a point marker with the own-vote value in the accessible name_D111', () => {
    const pollOptionEntries: GridPollOptionEntry[] = [
      { id: 'option-1', pollId: 'poll-1', pollTitle: 'Raid night poll', start: localDate(9, 0), ownVote: 'available' },
      { id: 'option-2', pollId: 'poll-2', pollTitle: 'Board game poll', start: localDate(10, 0), ownVote: null },
    ]
    renderGrid({ pollOptionEntries })

    // Adds no gridcell of its own — still 56 cells, one tab stop.
    expect(screen.getAllByRole('gridcell')).toHaveLength(56)

    const votedCell = screen.getByRole('gridcell', {
      name: '9:00 – Raid night poll poll option, your vote available',
    })
    expect(votedCell.querySelector('[data-layer="poll-option"]')).toHaveTextContent(
      'Raid night poll',
    )

    expect(
      screen.getByRole('gridcell', { name: '10:00 – Board game poll poll option' }),
    ).toBeInTheDocument()
  })

  it('renders a friend-count summary label when more than one friend overlaps a cell', () => {
    const friendRanges: GridFriendRange[] = [
      { ownerLabel: 'Ana', start: localDate(9, 0), end: localDate(9, 15) },
      { ownerLabel: 'Bruno', start: localDate(9, 0), end: localDate(9, 15) },
    ]
    renderGrid({ friendRanges })

    expect(screen.getByRole('gridcell', { name: '9:00 – 2 friends available' })).toBeInTheDocument()
  })
})

describe('WeekGrid pointer flow', () => {
  const occurrences: GridOccurrence[] = [
    { id: 'block-a', start: localDate(9, 0), end: localDate(9, 30) },
  ]

  // Catches a regression to `handleMouseDown`'s `occurrenceAt` bail (`:325`)
  // being the *only* mouse path — before the fix, a click on a block did
  // nothing because no click handler existed at all.
  it('opens the editor on a mouse click on an existing block', () => {
    const { onEditOccurrence, onCreateBlock } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    fireEvent.mouseDown(block, { button: 0 })
    fireEvent.mouseUp(block)
    fireEvent.click(block)

    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
    // A click on a block must never also start/commit a paint selection.
    expect(onCreateBlock).not.toHaveBeenCalled()
  })

  // Catches a regression to the missing touch entry point: before the fix,
  // `handlePointerUp` had no branch at all for "this was a short tap on an
  // existing block" — a phone user could not reach the editor.
  it('opens the editor on a touch tap on an existing block_P5TOUCH01', () => {
    const { onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    fireEvent.pointerDown(block, { pointerType: 'touch', clientX: 10, clientY: 10 })
    fireEvent.pointerUp(block, { pointerType: 'touch', clientX: 10, clientY: 10 })

    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })

  // Catches a regression where any touch liftoff on a block — including one
  // that dragged past the cancel threshold first (e.g. a scroll gesture
  // that happened to end over the block) — would be misread as a tap.
  it('does not open the editor when a touch moves past the cancel threshold before lifting_P5TOUCH01', () => {
    const { onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    fireEvent.pointerDown(block, { pointerType: 'touch', clientX: 10, clientY: 10 })
    fireEvent.pointerMove(block, { pointerType: 'touch', clientX: 10, clientY: 40 })
    fireEvent.pointerUp(block, { pointerType: 'touch', clientX: 10, clientY: 40 })

    expect(onEditOccurrence).not.toHaveBeenCalled()
  })

  // Regression guard: this already passes pre-change (the mouse-drag paint
  // path was never broken), included so the pointer suite proves the fix
  // didn't turn blocks into paint surfaces or otherwise disturb drag-to-paint
  // on empty cells.
  it('still paints a block by dragging across empty cells with the mouse', () => {
    const { onCreateBlock } = renderGrid({})

    const dragStart = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    const dragEnd = screen.getAllByRole('gridcell', { name: '9:30' })[0]
    fireEvent.mouseDown(dragStart, { button: 0 })
    fireEvent.mouseEnter(dragEnd)
    fireEvent.mouseUp(screen.getByRole('grid'))

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [range] = onCreateBlock.mock.calls[0]
    expect(range.start.getTime()).toBe(localDate(9, 0).getTime())
    expect(range.end.getTime()).toBe(localDate(9, 45).getTime())
  })

  // Regression guard: this already passes pre-change — `handleMouseDown`'s
  // `occurrenceAt` bail (`:325`) predates this ticket and is left untouched.
  // Included so the pointer suite proves a mouse drag still cannot start on
  // top of a block.
  it('does not begin a paint selection when a mouse drag starts on an existing block', () => {
    const { onCreateBlock, onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    const empty = screen.getAllByRole('gridcell', { name: '9:30' })[0]
    fireEvent.mouseDown(block, { button: 0 })
    fireEvent.mouseEnter(empty)
    fireEvent.mouseUp(screen.getByRole('grid'))

    expect(onCreateBlock).not.toHaveBeenCalled()
    // No click fired either (mousedown and mouseup targeted different
    // elements), so the editor doesn't open from this ambiguous gesture.
    expect(onEditOccurrence).not.toHaveBeenCalled()
  })

  // Regression guard: this already passes pre-change — Enter through
  // `handleKeyDown` (`:275`) is the path this ticket deliberately leaves
  // alone. Included so the pointer suite proves the new mouse/touch entry
  // points didn't disturb it. (`handleKeyDown`'s switch has no `' '`/Space
  // case at all — only Enter opens the editor from the keyboard today; see
  // report concerns.)
  it('still opens the editor with Enter from the keyboard', () => {
    const { onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    block.focus()
    fireEvent.keyDown(block, { key: 'Enter' })
    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })
})

function calendarResponse(
  occurrenceIso: { start: string; end: string },
  overrides: { friendAvailability?: unknown[]; sessions?: unknown[]; pollOptions?: unknown[] } = {},
) {
  return {
    myAvailability: {
      occurrences: [
        {
          startTime: occurrenceIso.start,
          endTime: occurrenceIso.end,
          origin: {
            kind: 'occurrence',
            blockId: null,
            seriesId: 'series-1',
            occurrenceDate: occurrenceIso.start.slice(0, 10),
          },
        },
      ],
      merged: [{ start: occurrenceIso.start, end: occurrenceIso.end }],
      range: { start: occurrenceIso.start, end: occurrenceIso.end },
    },
    friendAvailability: overrides.friendAvailability ?? [],
    sessions: overrides.sessions ?? [],
    pollOptions: overrides.pollOptions ?? [],
  }
}

function profileResponse(overrides: Record<string, unknown> = {}) {
  return {
    username: 'organizer',
    displayName: 'Organizer',
    avatar: null,
    timeZone: 'UTC',
    locale: 'en',
    friendCode: null,
    reminderDefault: 'Off',
    platformLinks: [],
    authMethods: [],
    ...overrides,
  }
}

function renderPage() {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={['/calendar']}>
        <WeekGridPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('WeekGridPage viewport', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-10T12:00:00.000Z'))
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse({
            start: '2026-08-10T13:00:00.000Z',
            end: '2026-08-10T13:30:00.000Z',
          }),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  // The grid's own default is startHour 0 — 96 rows the viewer has to scroll
  // past. `timeLabelFormatter` renders through `Intl` in the active locale, so
  // 07:00 reads "7:00 AM" and midnight "12:00 AM" under `en`.
  it('renders the day from 07:00 by default instead of midnight', async () => {
    renderPage()

    expect(await screen.findByText(/^7:00\s?AM$/)).toBeInTheDocument()
    expect(screen.queryByText(/^12:00\s?AM$/)).not.toBeInTheDocument()
  })

  it('reveals the early hours on request', async () => {
    renderPage()

    fireEvent.click(await screen.findByRole('button', { name: 'Show earlier hours' }))

    expect(await screen.findByText(/^12:00\s?AM$/)).toBeInTheDocument()
  })

  // WCAG 2.4.3: the control must survive its own activation, so focus stays on
  // it instead of falling to document.body, and the 28 revealed rows are
  // announced rather than appearing silently above the viewport.
  it('keeps the early-hours control focused and announces the change', async () => {
    renderPage()

    const toggle = await screen.findByRole('button', { name: 'Show earlier hours' })
    toggle.focus()
    fireEvent.click(toggle)

    const collapse = await screen.findByRole('button', { name: 'Hide earlier hours' })
    expect(collapse).toHaveFocus()
    expect(screen.getByTestId('early-hours-status')).toHaveTextContent(
      'Earlier hours shown. The grid now starts at midnight.',
    )

    fireEvent.click(collapse)

    expect(
      await screen.findByRole('button', { name: 'Show earlier hours' }),
    ).toHaveFocus()
    expect(screen.queryByText(/^12:00\s?AM$/)).not.toBeInTheDocument()
    expect(screen.getByTestId('early-hours-status')).toHaveTextContent(
      'Earlier hours hidden. The grid now starts in the morning.',
    )
  })
})

describe('WeekGridPage commit step', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-10T12:00:00.000Z'))
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse({
            start: '2026-08-10T13:00:00.000Z',
            end: '2026-08-10T13:30:00.000Z',
          }),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    api.post.mockResolvedValue({
      data: {
        blockId: 'block-new',
        seriesId: 'series-new',
        startTime: '2026-08-10T09:00:00.000Z',
        endTime: '2026-08-10T09:45:00.000Z',
        source: null,
      },
    })
    api.del.mockResolvedValue({ data: undefined })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  // The same mouse drag the pointer suite paints with, driven against the
  // page (startHour 7) rather than a bare grid — 9:00 AM is free in the
  // fixture, whose only occurrence sits at 13:00.
  async function paintRange() {
    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const dragStart = screen.getAllByRole('gridcell', { name: /^9:00\s?AM$/ })[0]
    const dragEnd = screen.getAllByRole('gridcell', { name: /^9:30\s?AM$/ })[0]
    fireEvent.mouseDown(dragStart, { button: 0 })
    fireEvent.mouseEnter(dragEnd)
    fireEvent.mouseUp(screen.getByRole('grid'))
  }

  it('asks for a recurrence choice before saving a painted range_D009', async () => {
    renderPage()
    await paintRange()

    expect(await screen.findByRole('dialog')).toHaveTextContent(
      'Add this availability?',
    )
    expect(api.post).not.toHaveBeenCalled()
  })

  it('creates a weekly series when the range repeats_D009', async () => {
    renderPage()
    await paintRange()

    fireEvent.click(await screen.findByRole('radio', { name: 'Repeats weekly' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    const [path, options] = api.post.mock.calls[0]
    expect(path).toBe('/api/v1/me/availability/series')
    expect(options.body).toEqual({
      // `en` starts its week on Sunday, so the first painted column is
      // 2026-08-09 — ISO 1..7 (NodaTime IsoDayOfWeek), Sunday = 7.
      dayOfWeek: 7,
      startTime: '09:00',
      durationMinutes: 45,
      effectiveFrom: '2026-08-09',
      effectiveUntil: null,
    })
  })

  it('creates a one-time block by default_D009', async () => {
    renderPage()
    await paintRange()

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(1))
    expect(api.post.mock.calls[0][0]).toBe('/api/v1/me/availability/blocks')
  })

  it('discards the range when the commit step is dismissed_D009', async () => {
    renderPage()
    await paintRange()

    fireEvent.click(await screen.findByRole('button', { name: 'Discard' }))

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(api.post).not.toHaveBeenCalled()
  })

  it('undoes a just-created weekly series_D009', async () => {
    renderPage()
    await paintRange()

    fireEvent.click(await screen.findByRole('radio', { name: 'Repeats weekly' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    fireEvent.click(await screen.findByRole('button', { name: 'Undo' }))

    await waitFor(() => expect(api.del).toHaveBeenCalledTimes(1))
    const [path, options] = api.del.mock.calls[0]
    expect(path).toBe('/api/v1/me/availability/series/{seriesId}')
    expect(options.params.path).toEqual({ seriesId: 'series-new' })
  })
})

describe('WeekGridPage occurrence editing', () => {
  beforeEach(() => {
    // Only `Date` is faked — leaving real timers so `findBy*`/`waitFor`
    // (which poll via `setTimeout`) still resolve.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-10T12:00:00.000Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('defaults the occurrence scope dialog to only this occurrence_D134_D143', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse({
            start: '2026-08-10T13:00:00.000Z',
            end: '2026-08-10T13:30:00.000Z',
          }),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    api.patch.mockResolvedValue({
      data: { scope: 'onlyThis', block: { blockId: 'block-x', startTime: '', endTime: '', source: null } },
    })

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const busyCell = screen.getAllByRole('gridcell', { name: /Busy/ })[0]
    busyCell.focus()
    fireEvent.keyDown(busyCell, { key: 'Enter' })

    const scopeDialog = await screen.findByRole('dialog')
    expect(scopeDialog).toHaveTextContent('Which occurrences?')
    expect(
      screen.getByRole('radio', { name: 'Only this occurrence' }),
    ).toBeChecked()
    expect(
      screen.getByRole('radio', { name: 'This and future occurrences' }),
    ).not.toBeChecked()

    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }))

    const editDialog = await screen.findByRole('dialog')
    expect(editDialog).toHaveTextContent('Edit availability')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(1))
    const [path, options] = api.patch.mock.calls[0]
    expect(path).toBe(
      '/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}',
    )
    expect(options.params.path).toEqual({
      seriesId: 'series-1',
      occurrenceDate: '2026-08-10',
    })
    expect(options.body.scope).toBe('onlyThis')
  })

  it('saves the thisAndFuture scope end to end, including the original-null response branch_D143', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse({
            start: '2026-08-10T13:00:00.000Z',
            end: '2026-08-10T13:30:00.000Z',
          }),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    // `original: null` — the §5.2/§8.1 branch where the split consumed the
    // whole original series (`SeriesSplitResult.OriginalBecameEmpty`); the
    // client must not assume `original` is ever present.
    api.patch.mockResolvedValue({
      data: {
        scope: 'thisAndFuture',
        original: null,
        successor: {
          seriesId: 'series-1',
          dayOfWeek: 2,
          startTime: '14:00',
          durationMinutes: 45,
          zoneId: 'UTC',
          effectiveFrom: '2026-08-11',
          effectiveUntil: null,
        },
      },
    })

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const busyCell = screen.getAllByRole('gridcell', { name: /Busy/ })[0]
    busyCell.focus()
    fireEvent.keyDown(busyCell, { key: 'Enter' })

    await screen.findByRole('dialog')
    fireEvent.click(screen.getByRole('radio', { name: 'This and future occurrences' }))
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }))

    const editDialog = await screen.findByRole('dialog')
    expect(editDialog).toHaveTextContent('Edit availability')

    // Move both the day and the time of day — the `dayOfWeek` may
    // legitimately differ from `occurrenceDate`'s weekday (design §8.1's
    // ruling, `DOMAIN_MODEL.md:196`).
    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-08-11T14:00' },
    })
    fireEvent.change(screen.getByLabelText('End time'), {
      target: { value: '2026-08-11T14:45' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(1))
    const [path, options] = api.patch.mock.calls[0]
    expect(path).toBe(
      '/api/v1/me/availability/series/{seriesId}/occurrences/{occurrenceDate}',
    )
    expect(options.params.path).toEqual({
      seriesId: 'series-1',
      occurrenceDate: '2026-08-10',
    })
    expect(options.body).toEqual({
      scope: 'thisAndFuture',
      dayOfWeek: 2, // Tuesday, ISO 1..7
      startTime: '14:00',
      durationMinutes: 45,
      endTime: null,
    })

    // The discriminated `original: null` response is handled without
    // throwing or leaving the dialog open, and without inventing UI for a
    // field that legitimately isn't there.
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('rejects an end time at or before the start time locally, without a server round trip_D071', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: {
            myAvailability: {
              occurrences: [
                {
                  startTime: '2026-08-10T13:00:00.000Z',
                  endTime: '2026-08-10T13:30:00.000Z',
                  origin: { kind: 'block', blockId: 'block-1', seriesId: null, occurrenceDate: null },
                },
              ],
              merged: [{ start: '2026-08-10T13:00:00.000Z', end: '2026-08-10T13:30:00.000Z' }],
              range: { start: '2026-08-10T13:00:00.000Z', end: '2026-08-10T13:30:00.000Z' },
            },
            friendAvailability: [],
            sessions: [],
            pollOptions: [],
          },
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const busyCell = screen.getAllByRole('gridcell', { name: /Busy/ })[0]
    busyCell.focus()
    fireEvent.keyDown(busyCell, { key: 'Enter' })

    // A one-time block (kind: 'block') opens the editor directly — no
    // scope dialog detour, matching handleEditOccurrence in WeekGridPage.tsx.
    const editDialog = await screen.findByRole('dialog')
    expect(editDialog).toHaveTextContent('Edit availability')

    fireEvent.change(screen.getByLabelText('End time'), {
      target: { value: '2026-08-10T13:00' }, // equal to the prefilled start — rejected locally
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(
      await screen.findByText('End time must be after the start time.'),
    ).toBeInTheDocument()
    expect(api.patch).not.toHaveBeenCalled()
    expect(editDialog).toBeVisible()
  })

  it.each([
    'availability_not_found',
    'availability_validation',
    'invalid_range',
    'range_too_large',
    'occurrence_not_found',
  ])('renders the availability.json message for %s', async (code) => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') return Promise.reject({ code })
      throw new Error(`Unexpected GET ${path}`)
    })

    renderPage()

    const expected: Record<string, string> = {
      availability_not_found: 'That availability entry could not be found.',
      availability_validation: 'Check the highlighted fields and try again.',
      invalid_range: 'That date range is not valid.',
      range_too_large: 'Choose a shorter date range.',
      occurrence_not_found: 'That occurrence could not be found.',
    }

    expect(await screen.findByRole('alert')).toHaveTextContent(expected[code])
  })

  it('paints friendAvailability and sessions on the grid, and layer toggles visibly add or remove them_A080', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse(
            { start: '2026-08-10T13:00:00.000Z', end: '2026-08-10T13:30:00.000Z' },
            {
              friendAvailability: [
                {
                  owner: { ordinal: 0, displayName: 'Ana' },
                  ranges: [{ start: '2026-08-10T14:00:00.000Z', end: '2026-08-10T14:15:00.000Z' }],
                },
              ],
              sessions: [
                {
                  id: 'session-1',
                  organizerId: null,
                  title: 'Raid night',
                  startTime: '2026-08-10T15:00:00.000Z',
                  durationMinutes: 15,
                  platform: '',
                  game: null,
                  status: 'unconfirmed',
                  isPrivate: false,
                  maxCapacity: null,
                  activeOccupancy: 1,
                  organizer: { ordinal: 1, displayName: 'Ana' },
                  viewerRelation: 'organizer',
                },
              ],
            },
          ),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    expect(screen.getByRole('gridcell', { name: /Ana available/ })).toBeInTheDocument()
    expect(screen.getByRole('gridcell', { name: /Raid night/ })).toBeInTheDocument()

    fireEvent.click(screen.getByLabelText("Friends' availability"))
    expect(screen.queryByRole('gridcell', { name: /Ana available/ })).not.toBeInTheDocument()
    // Toggling the friend layer never touches the sessions layer.
    expect(screen.getByRole('gridcell', { name: /Raid night/ })).toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Sessions'))
    expect(screen.queryByRole('gridcell', { name: /Raid night/ })).not.toBeInTheDocument()
  })

  it('binds the pollOptions layer to CalendarPollOptionView data, on the grid and in the layer legend, toggle-controlled_D111', async () => {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse(
            { start: '2026-08-10T13:00:00.000Z', end: '2026-08-10T13:30:00.000Z' },
            {
              pollOptions: [
                {
                  pollId: 'poll-1',
                  title: 'Raid night poll',
                  options: [
                    {
                      optionId: 'option-1',
                      startTime: '2026-08-10T16:00:00.000Z',
                      ownVote: 'available',
                    },
                  ],
                },
              ],
            },
          ),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    expect(
      screen.getByRole('gridcell', { name: /Raid night poll poll option, your vote available/ }),
    ).toBeInTheDocument()
    // The pollOptions legend swatch now has real data behind it — the poll
    // title appears once as a legend item too, from the same `GET /calendar`
    // response, never a second fetch.
    expect(screen.getByRole('listitem')).toHaveTextContent('Raid night poll')

    fireEvent.click(screen.getByLabelText('Poll options'))
    expect(
      screen.queryByRole('gridcell', { name: /Raid night poll/ }),
    ).not.toBeInTheDocument()
    expect(screen.queryByText('Raid night poll')).not.toBeInTheDocument()
  })
})
