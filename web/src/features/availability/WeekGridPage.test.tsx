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
    // Round 1's F5 finding: the true union of these two occurrences IS
    // 9:00–9:45, so a fixture using that union can't discriminate the
    // client trusting `merged` verbatim (A-076) from a client silently
    // re-deriving the same merge itself — both would paint identically.
    // `merged` is deliberately short of block-b's own tail here (an
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

  it('renders a friend-count summary label when more than one friend overlaps a cell', () => {
    const friendRanges: GridFriendRange[] = [
      { ownerLabel: 'Ana', start: localDate(9, 0), end: localDate(9, 15) },
      { ownerLabel: 'Bruno', start: localDate(9, 0), end: localDate(9, 15) },
    ]
    renderGrid({ friendRanges })

    expect(screen.getByRole('gridcell', { name: '9:00 – 2 friends available' })).toBeInTheDocument()
  })
})

function calendarResponse(
  occurrenceIso: { start: string; end: string },
  overrides: { friendAvailability?: unknown[]; sessions?: unknown[] } = {},
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
    pollOptions: [],
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

    await screen.findAllByRole('gridcell', { name: /Busy/ })
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

    await screen.findAllByRole('gridcell', { name: /Busy/ })
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

  it('rejects an end time at or before the start time locally, without a server round trip_F3', async () => {
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

    await screen.findAllByRole('gridcell', { name: /Busy/ })
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

    await screen.findAllByRole('gridcell', { name: /Busy/ })
    expect(screen.getByRole('gridcell', { name: /Ana available/ })).toBeInTheDocument()
    expect(screen.getByRole('gridcell', { name: /Raid night/ })).toBeInTheDocument()

    fireEvent.click(screen.getByLabelText("Friends' availability"))
    expect(screen.queryByRole('gridcell', { name: /Ana available/ })).not.toBeInTheDocument()
    // Toggling the friend layer never touches the sessions layer.
    expect(screen.getByRole('gridcell', { name: /Raid night/ })).toBeInTheDocument()

    fireEvent.click(screen.getByLabelText('Sessions'))
    expect(screen.queryByRole('gridcell', { name: /Raid night/ })).not.toBeInTheDocument()
  })
})
