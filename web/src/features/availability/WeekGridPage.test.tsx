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
  type GridAdjustment,
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
  onAdjustOccurrence?: ReturnType<typeof vi.fn<(adjustment: GridAdjustment) => void>>
  startHour?: number
  endHour?: number
  now?: Date | null
}) {
  const onCreateBlock = props.onCreateBlock ?? vi.fn<(range: GridRange) => void>()
  const onEditOccurrence = props.onEditOccurrence ?? vi.fn<(id: string) => void>()
  render(
    <WeekGrid
      dayLabels={dayLabels}
      endHour={props.endHour ?? 11}
      friendRanges={props.friendRanges ?? []}
      merged={props.merged ?? []}
      now={props.now ?? null}
      occurrences={props.occurrences ?? []}
      pollOptionEntries={props.pollOptionEntries ?? []}
      sessionEntries={props.sessionEntries ?? []}
      startHour={props.startHour ?? 9}
      timeLabelFormatter={timeLabelFormatter}
      weekStart={localDate(0)}
      onAdjustOccurrence={props.onAdjustOccurrence}
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
    // The commit step still has to be confirmed, so the live region must not
    // claim the availability already exists.
    expect(screen.getByRole('status')).toHaveTextContent(
      'Selected 9:00 to 9:30. Confirm how often it repeats.',
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
  // `handleKeyDown` is the path this ticket deliberately leaves alone.
  // Included so the pointer suite proves the new mouse/touch entry points
  // didn't disturb it. Space's identical behavior is covered separately in
  // the "WeekGrid Space activation" suite below.
  it('still opens the editor with Enter from the keyboard', () => {
    const { onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    block.focus()
    fireEvent.keyDown(block, { key: 'Enter' })
    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })
})

describe('WeekGrid resize handles', () => {
  const occurrences: GridOccurrence[] = [
    { id: 'block-a', start: localDate(9, 0), end: localDate(9, 30) },
  ]

  function selectBlock() {
    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    fireEvent.mouseDown(block, { button: 0 })
    fireEvent.mouseUp(block)
    fireEvent.click(block)
  }

  it('shows no resize handles until the block is selected', () => {
    renderGrid({
      occurrences,
      onAdjustOccurrence: vi.fn<(adjustment: GridAdjustment) => void>(),
    })

    expect(
      screen.queryByRole('button', { name: 'Change end time' }),
    ).not.toBeInTheDocument()
    expect(
      screen.queryByRole('button', { name: 'Change start time' }),
    ).not.toBeInTheDocument()
  })

  it('resizes a selected block by dragging its end edge', () => {
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    renderGrid({ occurrences, onAdjustOccurrence })

    selectBlock()

    const handle = screen.getByRole('button', { name: 'Change end time' })
    // jsdom reports a zero-height rect, so the grid falls back to the
    // min-h-11 floor: 100px is two 44px slots' worth of travel.
    fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0, clientY: 0 })
    fireEvent.pointerMove(handle, { pointerType: 'mouse', clientY: 100 })
    fireEvent.pointerUp(handle, { pointerType: 'mouse', clientY: 100 })

    expect(onAdjustOccurrence).toHaveBeenCalledTimes(1)
    const [adjustment] = onAdjustOccurrence.mock.calls[0]
    expect(adjustment.occurrenceId).toBe('block-a')
    expect(adjustment.start.getTime()).toBe(localDate(9, 0).getTime())
    expect(adjustment.end.getTime()).toBe(localDate(10, 0).getTime())
  })

  it('drags the start edge without moving the end', () => {
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    renderGrid({ occurrences, onAdjustOccurrence })

    selectBlock()

    const handle = screen.getByRole('button', { name: 'Change start time' })
    fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0, clientY: 100 })
    fireEvent.pointerUp(handle, { pointerType: 'mouse', clientY: 56 })

    const [adjustment] = onAdjustOccurrence.mock.calls[0]
    expect(adjustment.start.getTime()).toBe(localDate(8, 45).getTime())
    expect(adjustment.end.getTime()).toBe(localDate(9, 30).getTime())
  })

  it('never resizes a window down to nothing', () => {
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    renderGrid({ occurrences, onAdjustOccurrence })

    selectBlock()

    const handle = screen.getByRole('button', { name: 'Change end time' })
    fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0, clientY: 400 })
    fireEvent.pointerUp(handle, { pointerType: 'mouse', clientY: 0 })

    const [adjustment] = onAdjustOccurrence.mock.calls[0]
    expect(adjustment.end.getTime()).toBe(localDate(9, 15).getTime())
  })

  // The pointer affordance needs a keyboard equivalent that is not "reopen the
  // editor and retype" (WCAG 2.1.1).
  it('nudges the end edge one slot per arrow key', () => {
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    renderGrid({ occurrences, onAdjustOccurrence })

    selectBlock()

    const handle = screen.getByRole('button', { name: 'Change end time' })
    fireEvent.keyDown(handle, { key: 'ArrowDown' })

    const [adjustment] = onAdjustOccurrence.mock.calls[0]
    expect(adjustment.end.getTime()).toBe(localDate(9, 45).getTime())
  })

  // The handles are the only thing that may not steal a bare finger-drag:
  // P5-TOUCH-01 keeps the grid itself scrollable until a long-press fires.
  it('leaves the grid-level touch gating untouched_P5TOUCH01', () => {
    renderGrid({
      occurrences,
      onAdjustOccurrence: vi.fn<(adjustment: GridAdjustment) => void>(),
    })

    selectBlock()

    expect(screen.getByRole('grid')).toHaveAttribute('data-touch-dragging', 'false')
    expect(screen.getByRole('grid')).not.toHaveClass('touch-none')
    expect(screen.getByRole('button', { name: 'Change end time' })).toHaveClass(
      'touch-none',
    )
  })

  // A `touch-none` handle that overhangs its cell would freeze page scrolling
  // on a neighbouring free cell — the exact P5-TOUCH-01 failure, displaced.
  it('keeps each handle inside its own cell box_P5TOUCH01', () => {
    renderGrid({
      occurrences,
      onAdjustOccurrence: vi.fn<(adjustment: GridAdjustment) => void>(),
    })

    selectBlock()

    expect(screen.getByRole('button', { name: 'Change start time' })).toHaveClass('top-0')
    expect(screen.getByRole('button', { name: 'Change end time' })).toHaveClass('bottom-0')
    for (const name of ['Change start time', 'Change end time']) {
      const className = screen.getByRole('button', { name }).className
      expect(className).not.toMatch(/(^|\s)-(top|bottom|inset)-/)
    }
  })

  // Cancelling `pointerdown` does not cancel the `click`: without an explicit
  // guard the release bubbles to the gridcell and reopens the editor, which
  // overwrites the adjustment the drag just produced.
  it('does not reopen the editor after a pointer resize', () => {
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    const { onEditOccurrence } = renderGrid({ occurrences, onAdjustOccurrence })

    selectBlock()
    onEditOccurrence.mockClear()

    const handle = screen.getByRole('button', { name: 'Change end time' })
    fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0, clientY: 0 })
    fireEvent.pointerMove(handle, { pointerType: 'mouse', clientY: 100 })
    fireEvent.pointerUp(handle, { pointerType: 'mouse', clientY: 100 })
    fireEvent.click(handle)

    expect(onAdjustOccurrence).toHaveBeenCalledTimes(1)
    expect(onEditOccurrence).not.toHaveBeenCalled()
  })

  // A block only one or two slots tall is covered edge to edge by its own
  // handles, so the press that never travelled has to stay a plain tap.
  it('treats a press that never moved as a tap on the block', () => {
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    const { onEditOccurrence } = renderGrid({ occurrences, onAdjustOccurrence })

    selectBlock()
    onEditOccurrence.mockClear()

    const handle = screen.getByRole('button', { name: 'Change end time' })
    fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0, clientY: 40 })
    fireEvent.pointerUp(handle, { pointerType: 'mouse', clientY: 40 })
    fireEvent.click(handle)

    expect(onAdjustOccurrence).not.toHaveBeenCalled()
    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })

  // §8: a native `<button>`'s Space/Enter would otherwise synthesize a click
  // that bubbles to the cell and reopens the editor on top of whichever
  // pointer-driven adjustment just happened; `handleResizeKeyDown` swallows
  // both so that click is never produced. jsdom doesn't synthesize that
  // click from a keydown the way a real browser does, so the assertion here
  // is on the swallow itself: the keydown is prevented and no arrow-key
  // nudge logic runs.
  it.each(['Enter', ' '])(
    'swallows %s on the resize handle without nudging or double-activating',
    (key) => {
      const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
      renderGrid({ occurrences, onAdjustOccurrence })

      selectBlock()

      const handle = screen.getByRole('button', { name: 'Change end time' })
      const notPrevented = fireEvent.keyDown(handle, { key })

      expect(notPrevented).toBe(false)
      expect(onAdjustOccurrence).not.toHaveBeenCalled()
    },
  )
})

describe('WeekGrid Space activation_D071_D132', () => {
  it('mirrors Enter through the full keyboard selection flow with Space', () => {
    const { onCreateBlock } = renderGrid({})

    const start = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    start.focus()
    fireEvent.keyDown(start, { key: ' ' })
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')

    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    const extended = screen.getAllByRole('gridcell', { name: '9:15' })[0]
    fireEvent.keyDown(extended, { key: ' ' })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [committed] = onCreateBlock.mock.calls[0]
    expect(committed.start.getTime()).toBe(localDate(9, 0).getTime())
    expect(committed.end.getTime()).toBe(localDate(9, 30).getTime())
  })

  it('opens the editor with Space on an existing block', () => {
    const occurrences: GridOccurrence[] = [
      { id: 'block-a', start: localDate(9, 0), end: localDate(9, 30) },
    ]
    const { onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    block.focus()
    fireEvent.keyDown(block, { key: ' ' })

    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })

  it('cancels a selection with Escape after starting it with Space', () => {
    renderGrid({})

    const cell = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    cell.focus()
    fireEvent.keyDown(cell, { key: ' ' })
    fireEvent.keyDown(cell, { key: 'Escape' })

    expect(screen.getByRole('status')).toHaveTextContent('Selection canceled.')
  })

  it('prevents the default (page scroll) on Space', () => {
    renderGrid({})

    const cell = screen.getAllByRole('gridcell', { name: '9:00' })[0]
    cell.focus()
    const notPrevented = fireEvent.keyDown(cell, { key: ' ' })

    expect(notPrevented).toBe(false)
  })
})

describe('WeekGrid §8 band rendering', () => {
  it('rounds a band at its true edges and flattens a clamped edge', () => {
    // block-a is entirely within the visible 9:00-11:00 window, so both its
    // true start and end are rounded.
    const occurrences: GridOccurrence[] = [
      { id: 'block-a', start: localDate(9, 0), end: localDate(9, 45) },
    ]
    const merged: GridRange[] = [{ start: localDate(9, 0), end: localDate(9, 45) }]
    renderGrid({ occurrences, merged })

    const startCell = screen.getByRole('gridcell', { name: '9:00 – Busy' })
    const middleCell = screen.getByRole('gridcell', { name: '9:15 – Busy' })
    const endCell = screen.getByRole('gridcell', { name: '9:30 – Busy' })

    expect(startCell.className).toMatch(/rounded-t-lg/)
    expect(startCell.className).not.toMatch(/border-t-0/)
    // The internal 15-minute border between two cells of the same band is
    // suppressed on every cell but the run's start.
    expect(middleCell.className).toMatch(/border-t-0/)
    expect(middleCell.className).not.toMatch(/rounded-(t|b)-lg/)
    expect(endCell.className).toMatch(/border-t-0/)
    expect(endCell.className).toMatch(/rounded-b-lg/)
  })

  it('flattens the visible edge when the band continues past the viewport', () => {
    // block-a starts an hour before the grid's visible 9:00 floor, so the
    // first visible slot is a clamp, not the window's true start.
    const occurrences: GridOccurrence[] = [
      { id: 'block-a', start: localDate(8, 0), end: localDate(9, 30) },
    ]
    const merged: GridRange[] = [{ start: localDate(8, 0), end: localDate(9, 30) }]
    renderGrid({ occurrences, merged })

    const firstVisibleCell = screen.getByRole('gridcell', { name: '9:00 – Busy' })
    expect(firstVisibleCell.className).not.toMatch(/rounded-t-lg/)
  })

  it('renders a session title once per band, at the band start only', () => {
    const sessionEntries: GridSessionEntry[] = [
      {
        id: 'session-1',
        title: 'Raid night',
        start: localDate(9, 0),
        end: localDate(9, 30),
        viewerRelation: 'organizer',
      },
    ]
    renderGrid({ sessionEntries })

    const startCell = screen.getByRole('gridcell', { name: '9:00 – Raid night' })
    const middleCell = screen.getByRole('gridcell', { name: '9:15 – Raid night' })
    expect(startCell.querySelector('[data-layer="session"]')).toHaveTextContent('Raid night')
    expect(middleCell.querySelector('[data-layer="session"]')).toBeNull()
  })

  it('renders the friend glyph once per coverage segment, at the segment start only', () => {
    const friendRanges: GridFriendRange[] = [
      { ownerLabel: 'Ana', start: localDate(9, 0), end: localDate(9, 30) },
    ]
    renderGrid({ friendRanges })

    const startCell = screen.getByRole('gridcell', { name: '9:00 – Ana available' })
    const middleCell = screen.getByRole('gridcell', { name: '9:15 – Ana available' })
    expect(startCell.querySelector('[data-layer="friend"]')).not.toBeNull()
    expect(middleCell.querySelector('[data-layer="friend"]')).toBeNull()
  })

  it('shifts a band label off the start cell once a resize handle occupies it', () => {
    const occurrences: GridOccurrence[] = [
      { id: 'block-a', start: localDate(9, 0), end: localDate(9, 30) },
    ]
    const sessionEntries: GridSessionEntry[] = [
      {
        id: 'session-1',
        title: 'Raid night',
        start: localDate(9, 0),
        end: localDate(9, 30),
        viewerRelation: 'organizer',
      },
    ]
    const onAdjustOccurrence = vi.fn<(adjustment: GridAdjustment) => void>()
    renderGrid({ occurrences, sessionEntries, onAdjustOccurrence })

    const startCell = screen.getAllByRole('gridcell', { name: '9:00 – Raid night' })[0]
    expect(startCell.querySelector('[data-layer="session"]')).toHaveTextContent('Raid night')

    // Selecting the block exposes the start-cell resize handle, which would
    // otherwise occlude the title rendered underneath it.
    fireEvent.mouseDown(startCell, { button: 0 })
    fireEvent.mouseUp(startCell)
    fireEvent.click(startCell)

    expect(
      screen.getByRole('button', { name: 'Change start time' }),
    ).toBeInTheDocument()
    expect(startCell.querySelector('[data-layer="session"]')).toBeNull()
    const secondCell = screen.getByRole('gridcell', { name: '9:15 – Raid night' })
    expect(secondCell.querySelector('[data-layer="session"]')).toHaveTextContent('Raid night')
  })
})

describe('WeekGrid §9 now-line', () => {
  it('marks the current time on today’s column only, inside the visible hours', () => {
    // weekStart is 2026-08-10 (day 0), so 2026-08-11 09:30 is day 1 — the
    // grid derives that purely by diffing the injected fake-local `now`
    // against weekStart, never from the host clock.
    renderGrid({ now: localDate(9, 30, 11) })

    const markers = document.querySelectorAll('[data-layer="now"]')
    expect(markers).toHaveLength(1)
    const cell = markers[0].closest('[role="gridcell"]')
    expect(cell).toHaveAttribute('aria-label', '9:30')
    expect(cell).toHaveAttribute('aria-colindex', '3')
  })

  it('is presentational only, hidden from assistive technology', () => {
    // 09:37:30 is half way through the 9:30–9:45 slot.
    renderGrid({ now: new Date(Date.UTC(2026, 7, 11, 9, 37, 30, 0)) })

    const marker = document.querySelector('[data-layer="now"]')
    expect(marker).toHaveAttribute('aria-hidden', 'true')
    // Positioned within the slot it falls in, not pinned to a cell edge.
    expect((marker as HTMLElement).style.top).toBe('50%')
    expect(marker?.closest('[role="gridcell"]')).toHaveAttribute('aria-label', '9:30')
  })

  it('renders nothing when now falls outside the visible hour range', () => {
    // 07:00 is before the grid's 9:00 floor; the day is still in the week.
    renderGrid({ now: localDate(7, 0, 11) })

    expect(document.querySelectorAll('[data-layer="now"]')).toHaveLength(0)
  })

  it('renders nothing when now falls outside the displayed week', () => {
    renderGrid({ now: localDate(9, 30, 20) })

    expect(document.querySelectorAll('[data-layer="now"]')).toHaveLength(0)
  })

  it('renders nothing when no clock is injected', () => {
    renderGrid({})

    expect(document.querySelectorAll('[data-layer="now"]')).toHaveLength(0)
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

  // The grid's own default is startHour 0 — 96 rows. The page clamps the
  // default window to 08:00–24:00. `timeLabelFormatter` renders through `Intl`
  // in the active locale, so 08:00 reads "8:00 AM" and midnight "12:00 AM"
  // under `en`.
  it('renders the day from 08:00 by default instead of midnight', async () => {
    renderPage()

    expect(await screen.findByText(/^8:00\s?AM$/)).toBeInTheDocument()
    expect(screen.queryByText(/^7:00\s?AM$/)).not.toBeInTheDocument()
    expect(screen.queryByText(/^12:00\s?AM$/)).not.toBeInTheDocument()
  })

  // The grid is no longer an internally scrollable element: it renders at its
  // natural height (64 rows by default) and the document scrolls. A height cap
  // or a vertical overflow rule here would restore the nested scrollport.
  it('renders the grid at natural height instead of its own scrollport', async () => {
    renderPage()

    const grid = await screen.findByRole('grid')
    expect(grid.className).not.toMatch(/max-h-/)
    expect(grid.className).not.toMatch(/overflow-y-(auto|scroll)/)
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

describe('WeekGridPage now-line clock', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-10T12:00:00.000Z'))
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') {
        return Promise.resolve({ data: profileResponse({ timeZone: 'America/New_York' }) })
      }
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: calendarResponse({
            start: '2026-08-10T18:00:00.000Z',
            end: '2026-08-10T18:30:00.000Z',
          }),
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  // The page owns the clock and the zone: 12:00Z is 08:00 in the viewer's
  // profile zone, so the marker lands on the 8:00 AM slot — proof the value
  // crossed the shipped `instantToFakeLocal` boundary rather than being read
  // off the host's own local time inside the grid.
  it('passes a fake-local now through to the grid_D046', async () => {
    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })

    const markers = document.querySelectorAll('[data-layer="now"]')
    expect(markers).toHaveLength(1)
    const cell = markers[0].closest('[role="gridcell"]')
    expect(cell?.getAttribute('aria-label')).toMatch(/^8:00\s?AM$/)
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
  // page (startHour 8) rather than a bare grid — 9:00 AM is free in the
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

  it('names the toast region through i18next and offers an explicit dismiss', async () => {
    renderPage()
    await paintRange()

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    await screen.findByRole('button', { name: 'Undo' })

    expect(screen.getByRole('region', { name: 'Notifications (F8)' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: 'Undo' })).not.toBeInTheDocument(),
    )
  })

  // Radix auto-closes a toast after 5s by default; the undo affordance lives
  // only here, so it must survive that (WCAG 2.2.1). Real timers: the toast
  // timer is Radix-internal, so the only honest check is to outlast it.
  it('keeps the undo toast open past the default toast timeout', async () => {
    renderPage()
    await paintRange()

    fireEvent.click(await screen.findByRole('button', { name: 'Save' }))
    await screen.findByRole('button', { name: 'Undo' })

    await new Promise((resolve) => {
      setTimeout(resolve, 5_500)
    })

    expect(screen.getByRole('button', { name: 'Undo' })).toBeInTheDocument()
  }, 20_000)
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

  it('routes a resize on a recurring occurrence through the scope dialog_D025', async () => {
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

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const busyCell = screen.getAllByRole('gridcell', { name: /Busy/ })[0]
    fireEvent.click(busyCell)

    // Activation selects the occurrence and opens the scope dialog; dismissing
    // it leaves the selection (and so the handles) in place.
    await screen.findByRole('dialog')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    const handle = await screen.findByRole('button', { name: 'Change end time' })
    fireEvent.pointerDown(handle, { pointerType: 'mouse', button: 0, clientY: 0 })
    fireEvent.pointerUp(handle, { pointerType: 'mouse', clientY: 100 })
    // A real browser dispatches this after the release; if it reaches the
    // gridcell the editor reopens on the pre-drag times and the drag is lost.
    fireEvent.click(handle)

    expect(await screen.findByRole('dialog')).toHaveTextContent(
      'Only this occurrence',
    )
    expect(api.patch).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }))
    await waitFor(() =>
      expect(screen.getByLabelText('End time')).toHaveValue('2026-08-10T14:00'),
    )
  })

  // D-132/D-139: the editor's own close is what hands focus back to the grid
  // cell that opened it (`BlockEditorDialog`'s `onCloseAutoFocus`). An edit
  // repaints the block onto different cells, so closing on the PATCH response —
  // before the calendar refetch has landed — returns a keyboard viewer to a cell
  // that is about to stop being their block, and `Enter` there starts painting a
  // new selection instead of reopening it. Found on Firefox 151 by the D-138
  // two-engine lane, where the slower refetch loses that race every time.
  it('keeps the editor open until the calendar refetch lands, so focus never returns into a stale grid_D132_D139', async () => {
    let releaseRefetch: (() => void) | null = null
    let calendarCalls = 0
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        calendarCalls += 1
        const payload = {
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
        }
        // The post-edit refetch is held open by hand: the assertion below is
        // exactly that the dialog is still up while it is in flight.
        if (calendarCalls > 1) {
          return new Promise((resolve) => {
            releaseRefetch = () => resolve(payload)
          })
        }
        return Promise.resolve(payload)
      }
      throw new Error(`Unexpected GET ${path}`)
    })
    api.patch.mockResolvedValue({ data: { blockId: 'block-1' } })

    renderPage()

    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const busyCell = screen.getAllByRole('gridcell', { name: /Busy/ })[0]
    busyCell.focus()
    fireEvent.keyDown(busyCell, { key: 'Enter' })

    const editDialog = await screen.findByRole('dialog')
    expect(editDialog).toHaveTextContent('Edit availability')

    fireEvent.change(screen.getByLabelText('Start time'), {
      target: { value: '2026-08-10T15:00' },
    })
    fireEvent.change(screen.getByLabelText('End time'), {
      target: { value: '2026-08-10T15:30' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    // The write itself has already gone out and been answered...
    await waitFor(() => expect(api.patch).toHaveBeenCalledTimes(1))
    await waitFor(() => expect(releaseRefetch).not.toBeNull())
    // ...yet the editor is still up, because the grid behind it is still stale.
    expect(screen.getByRole('dialog')).toHaveTextContent('Edit availability')

    releaseRefetch!()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
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
