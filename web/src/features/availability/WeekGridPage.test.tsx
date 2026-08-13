import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { useState } from 'react'
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
import { clampPopoverPosition } from './CommitBlockDialog'
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
  onCreateBlock?: ReturnType<typeof vi.fn<(range: GridRange, anchor: DOMRect) => void>>
  onEditOccurrence?: ReturnType<typeof vi.fn<(id: string) => void>>
  onAdjustOccurrence?: ReturnType<typeof vi.fn<(adjustment: GridAdjustment) => void>>
  startHour?: number
  endHour?: number
  now?: Date | null
  dayWindow?: { start: number; length: number }
}) {
  const onCreateBlock = props.onCreateBlock ?? vi.fn<(range: GridRange, anchor: DOMRect) => void>()
  const onEditOccurrence = props.onEditOccurrence ?? vi.fn<(id: string) => void>()
  render(
    <WeekGrid
      dayLabels={dayLabels}
      dayWindow={props.dayWindow}
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

    const start = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')

    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    expect(screen.getByRole('status')).toHaveTextContent('Selected 9:00 to 9:30.')

    const extended = screen.getAllByRole('gridcell', { name: 'Mon 9:15' })[0]
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

    const another = screen.getAllByRole('gridcell', { name: 'Mon 10:00' })[0]
    another.focus()
    fireEvent.keyDown(another, { key: 'Enter' })
    fireEvent.keyDown(another, { key: 'Escape' })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    expect(screen.getByRole('status')).toHaveTextContent('Selection canceled.')
  })

  it('snaps both entry modes to fifteen minutes_D071', () => {
    const { onCreateBlock } = renderGrid({})

    const keyboardCell = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    keyboardCell.focus()
    fireEvent.keyDown(keyboardCell, { key: 'Enter' })
    fireEvent.keyDown(keyboardCell, { key: 'ArrowDown', shiftKey: true })
    fireEvent.keyDown(keyboardCell, { key: 'Enter' })

    const [keyboardRange] = onCreateBlock.mock.calls[0]
    expect(keyboardRange.start.getUTCMinutes() % 15).toBe(0)
    expect(keyboardRange.end.getUTCMinutes() % 15).toBe(0)
    expect((keyboardRange.end.getTime() - keyboardRange.start.getTime()) % (15 * 60_000)).toBe(0)

    onCreateBlock.mockClear()

    const dragStart = screen.getAllByRole('gridcell', { name: 'Mon 10:00' })[0]
    const dragEnd = screen.getAllByRole('gridcell', { name: 'Mon 10:30' })[0]
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
      expect(screen.getByRole('gridcell', { name: `Mon ${label} – Busy` })).toHaveAttribute(
        'data-state',
        'busy',
      )
    }
    const nineThirty = screen.getAllByRole('gridcell', { name: 'Mon 9:30' })[0]
    expect(nineThirty).toHaveAttribute('data-state', 'free')

    // Both occurrences stay separately addressable for edit, including the
    // one `merged` doesn't fully paint.
    fireEvent.keyDown(screen.getByRole('gridcell', { name: 'Mon 9:00 – Busy' }), { key: 'Enter' })
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
    const ownSessionCell = screen.getByRole('gridcell', { name: 'Mon 9:00 – Ana available – Raid night' })
    expect(ownSessionCell.querySelector('[data-layer="friend"]')).not.toBeNull()
    const ownSessionLabel = ownSessionCell.querySelector('[data-layer="session"]')
    expect(ownSessionLabel).toHaveAttribute('data-viewer-relation', 'organizer')
    expect(ownSessionLabel).toHaveTextContent('Raid night')

    // The inherited/discoverable row is visibly distinct, and not by color
    // alone — a literal text label, not a class-only difference.
    const inheritedCell = screen.getByRole('gridcell', {
      name: 'Mon 10:00 – Open lobby (discoverable)',
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
      name: 'Mon 9:00 – Raid night poll poll option, your vote available',
    })
    expect(votedCell.querySelector('[data-layer="poll-option"]')).toHaveTextContent(
      'Raid night poll',
    )

    expect(
      screen.getByRole('gridcell', { name: 'Mon 10:00 – Board game poll poll option' }),
    ).toBeInTheDocument()
  })

  it('renders a friend-count summary label when more than one friend overlaps a cell', () => {
    const friendRanges: GridFriendRange[] = [
      { ownerLabel: 'Ana', start: localDate(9, 0), end: localDate(9, 15) },
      { ownerLabel: 'Bruno', start: localDate(9, 0), end: localDate(9, 15) },
    ]
    renderGrid({ friendRanges })

    expect(screen.getByRole('gridcell', { name: 'Mon 9:00 – 2 friends available' })).toBeInTheDocument()
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

    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
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

    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    fireEvent.pointerDown(block, { pointerType: 'touch', clientX: 10, clientY: 10 })
    fireEvent.pointerUp(block, { pointerType: 'touch', clientX: 10, clientY: 10 })

    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })

  // Catches a regression where any touch liftoff on a block — including one
  // that dragged past the cancel threshold first (e.g. a scroll gesture
  // that happened to end over the block) — would be misread as a tap.
  it('does not open the editor when a touch moves past the cancel threshold before lifting_P5TOUCH01', () => {
    const { onEditOccurrence } = renderGrid({ occurrences })

    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
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

    const dragStart = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    const dragEnd = screen.getAllByRole('gridcell', { name: 'Mon 9:30' })[0]
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

    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    const empty = screen.getAllByRole('gridcell', { name: 'Mon 9:30' })[0]
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

    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    block.focus()
    fireEvent.keyDown(block, { key: 'Enter' })
    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })
})

// CALENDAR_DESIGN.md §9 S3: `onCreateBlock`'s second argument anchors
// `CommitBlockDialog` at the commit point — a pointer/mouse release passes
// the pointer coordinates, a keyboard or touch commit passes the
// focused/last-selected cell's own measured rect.
describe('WeekGrid commit anchor_S3', () => {
  // Per element, never on `HTMLElement.prototype`: a prototype-wide stub makes
  // every cell measure the same, so the assertion could only prove that *a*
  // rect was taken — this pins the anchor to the specific cell measured.
  function stubCellRect(cell: HTMLElement, top: number): DOMRect {
    const rect = {
      x: 10,
      y: top,
      width: 44,
      height: 44,
      top,
      left: 10,
      right: 54,
      bottom: top + 44,
      toJSON: () => ({}),
    } as DOMRect
    vi.spyOn(cell, 'getBoundingClientRect').mockReturnValue(rect)
    return rect
  }

  it('passes the pointer release coordinates as the anchor for a mouse commit', () => {
    const { onCreateBlock } = renderGrid({})

    const dragStart = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    fireEvent.mouseDown(dragStart, { button: 0 })
    fireEvent.mouseUp(screen.getByRole('grid'), { clientX: 111, clientY: 222 })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [, anchor] = onCreateBlock.mock.calls[0]
    expect(anchor.left).toBe(111)
    expect(anchor.top).toBe(222)
    expect(anchor.width).toBe(0)
  })

  it('passes the focused cell rect as the anchor for a keyboard commit', () => {
    const { onCreateBlock } = renderGrid({})

    const start = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    const extended = screen.getAllByRole('gridcell', { name: 'Mon 9:15' })[0]
    const startRect = stubCellRect(start, 20)
    const extendedRect = stubCellRect(extended, 64)

    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })
    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    fireEvent.keyDown(extended, { key: 'Enter' })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [, anchor] = onCreateBlock.mock.calls[0]
    expect(anchor).toBe(extendedRect)
    expect(anchor).not.toBe(startRect)
  })

  it('passes the release cell rect as the anchor for a touch commit_P5TOUCH01', () => {
    vi.useFakeTimers()
    const { onCreateBlock } = renderGrid({})

    const start = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    const release = screen.getAllByRole('gridcell', { name: 'Mon 9:15' })[0]
    const startRect = stubCellRect(start, 5)
    const releaseRect = stubCellRect(release, 49)

    fireEvent.pointerDown(start, { pointerType: 'touch', clientX: 5, clientY: 5 })
    // Flushes the long-press timer's state update to a committed render
    // before the liftoff event — matching a real long-press, where the two
    // native events are separated by a real setTimeout tick.
    act(() => {
      vi.advanceTimersByTime(500)
    })
    fireEvent.pointerMove(release, { pointerType: 'touch', clientX: 5, clientY: 49 })
    fireEvent.pointerUp(release, { pointerType: 'touch', clientX: 5, clientY: 49 })

    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [, anchor] = onCreateBlock.mock.calls[0]
    expect(anchor).toBe(releaseRect)
    expect(anchor).not.toBe(startRect)

    vi.useRealTimers()
  })
})

describe('WeekGrid resize handles', () => {
  const occurrences: GridOccurrence[] = [
    { id: 'block-a', start: localDate(9, 0), end: localDate(9, 30) },
  ]

  function selectBlock() {
    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
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

    const start = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    start.focus()
    fireEvent.keyDown(start, { key: ' ' })
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')

    fireEvent.keyDown(start, { key: 'ArrowDown', shiftKey: true })
    const extended = screen.getAllByRole('gridcell', { name: 'Mon 9:15' })[0]
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

    const block = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    block.focus()
    fireEvent.keyDown(block, { key: ' ' })

    expect(onEditOccurrence).toHaveBeenCalledWith('block-a')
  })

  it('cancels a selection with Escape after starting it with Space', () => {
    renderGrid({})

    const cell = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
    cell.focus()
    fireEvent.keyDown(cell, { key: ' ' })
    fireEvent.keyDown(cell, { key: 'Escape' })

    expect(screen.getByRole('status')).toHaveTextContent('Selection canceled.')
  })

  it('prevents the default (page scroll) on Space', () => {
    renderGrid({})

    const cell = screen.getAllByRole('gridcell', { name: 'Mon 9:00' })[0]
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

    const startCell = screen.getByRole('gridcell', { name: 'Mon 9:00 – Busy' })
    const middleCell = screen.getByRole('gridcell', { name: 'Mon 9:15 – Busy' })
    const endCell = screen.getByRole('gridcell', { name: 'Mon 9:30 – Busy' })

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

    const firstVisibleCell = screen.getByRole('gridcell', { name: 'Mon 9:00 – Busy' })
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

    const startCell = screen.getByRole('gridcell', { name: 'Mon 9:00 – Raid night' })
    const middleCell = screen.getByRole('gridcell', { name: 'Mon 9:15 – Raid night' })
    expect(startCell.querySelector('[data-layer="session"]')).toHaveTextContent('Raid night')
    expect(middleCell.querySelector('[data-layer="session"]')).toBeNull()
  })

  it('renders the friend glyph once per coverage segment, at the segment start only', () => {
    const friendRanges: GridFriendRange[] = [
      { ownerLabel: 'Ana', start: localDate(9, 0), end: localDate(9, 30) },
    ]
    renderGrid({ friendRanges })

    const startCell = screen.getByRole('gridcell', { name: 'Mon 9:00 – Ana available' })
    const middleCell = screen.getByRole('gridcell', { name: 'Mon 9:15 – Ana available' })
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

    const startCell = screen.getAllByRole('gridcell', { name: 'Mon 9:00 – Raid night' })[0]
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
    const secondCell = screen.getByRole('gridcell', { name: 'Mon 9:15 – Raid night' })
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
    expect(cell).toHaveAttribute('aria-label', 'Tue 9:30')
    expect(cell).toHaveAttribute('aria-colindex', '3')
  })

  it('is presentational only, hidden from assistive technology', () => {
    // 09:37:30 is half way through the 9:30–9:45 slot.
    renderGrid({ now: new Date(Date.UTC(2026, 7, 11, 9, 37, 30, 0)) })

    const marker = document.querySelector('[data-layer="now"]')
    expect(marker).toHaveAttribute('aria-hidden', 'true')
    // Positioned within the slot it falls in, not pinned to a cell edge.
    expect((marker as HTMLElement).style.top).toBe('50%')
    expect(marker?.closest('[role="gridcell"]')).toHaveAttribute('aria-label', 'Tue 9:30')
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

function renderPage(initialEntries: string[] = ['/calendar']) {
  return render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <MemoryRouter initialEntries={initialEntries}>
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
    expect(cell?.getAttribute('aria-label')).toMatch(/^Mon 8:00\s?AM$/)
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
  // `dayLabel` names the column to paint in: at `sm`+ the whole week renders
  // and the fixture week's first column (Sunday, `en`) is used, but the §9 S5
  // day window below `sm` shows only Mon–Wed around the fixture's today, so
  // the bottom-sheet case has to paint inside that window.
  async function paintRange(dayLabel = 'Sun') {
    await screen.findAllByRole('gridcell', { name: /Busy/ }, { timeout: 5000 })
    const dragStart = screen.getAllByRole('gridcell', {
      name: new RegExp(`^${dayLabel} 9:00\\s?AM$`),
    })[0]
    const dragEnd = screen.getAllByRole('gridcell', {
      name: new RegExp(`^${dayLabel} 9:30\\s?AM$`),
    })[0]
    fireEvent.mouseDown(dragStart, { button: 0 })
    fireEvent.mouseEnter(dragEnd)
    fireEvent.mouseUp(screen.getByRole('grid'))
  }

  // §9 S3: the dialog is labelled by the range text itself (no generic
  // "add this?" heading) — querying by accessible name proves Radix's
  // aria-labelledby still resolves to it after the restyle.
  it('asks for a recurrence choice before saving a painted range_D009', async () => {
    renderPage()
    await paintRange()

    expect(
      await screen.findByRole('dialog', { name: /^09:00\s?AM.09:45\s?AM$/ }),
    ).toBeInTheDocument()
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

    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

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

  it('closes on Escape without saving_S3', async () => {
    renderPage()
    await paintRange()

    const dialog = await screen.findByRole('dialog')
    fireEvent.keyDown(dialog, { key: 'Escape', code: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(api.post).not.toHaveBeenCalled()
  })

  it('renders as a ≥sm popover clamped inside the viewport_S3', async () => {
    renderPage()
    await paintRange()

    const dialog = await screen.findByRole('dialog')
    expect(dialog.className).toMatch(/\bfixed\b/)
    expect(dialog.className).toMatch(/w-\[280px\]/)
    const { top, left } = clampPopoverPosition(
      { left: 0, top: 0, width: 0, height: 0 } as DOMRect,
      { width: window.innerWidth, height: window.innerHeight },
    )
    expect(dialog.style.top).toBe(`${top}px`)
    expect(dialog.style.left).toBe(`${left}px`)
    expect(parseFloat(dialog.style.left)).toBeGreaterThanOrEqual(0)
    expect(parseFloat(dialog.style.top)).toBeGreaterThanOrEqual(0)
  })

  // Below `sm` the same dialog is a bottom sheet — fixed to the bottom edge,
  // rounded top corners, and a grab-handle bar instead of a floating card.
  it('renders as a bottom sheet below sm_S3', async () => {
    const originalMatchMedia = window.matchMedia
    const media = {
      matches: true,
      media: '(max-width: 639px)',
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: vi.fn().mockReturnValue(media),
    })

    renderPage()
    await paintRange('Mon')

    const dialog = await screen.findByRole('dialog')
    expect(dialog.className).toMatch(/bottom-0/)
    expect(dialog.className).toMatch(/rounded-t-/)
    expect(dialog.querySelector('[aria-hidden="true"]')).toBeInTheDocument()

    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: originalMatchMedia,
    })
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

// CALENDAR_DESIGN.md §9 S4: side-panel (sticky aside) + main column at `lg`+,
// summary behind a mobile disclosure below `lg` — for both view branches.
describe('WeekGridPage §9 S4 aside layout', () => {
  function mockCalendar(overrides: { series?: unknown[]; blockOccurrence?: boolean } = {}) {
    api.get.mockImplementation((path: string) => {
      if (path === '/api/v1/me') return Promise.resolve({ data: profileResponse() })
      if (path === '/api/v1/calendar') {
        return Promise.resolve({
          data: {
            myAvailability: {
              occurrences: overrides.blockOccurrence
                ? [
                    {
                      startTime: '2026-08-10T13:00:00.000Z',
                      endTime: '2026-08-10T13:30:00.000Z',
                      origin: { kind: 'block', blockId: 'block-1', seriesId: null, occurrenceDate: null },
                    },
                  ]
                : [],
              merged: [],
              range: { start: '2026-08-10T00:00:00.000Z', end: '2026-08-17T00:00:00.000Z' },
              series: overrides.series ?? [],
            },
            friendAvailability: [],
            sessions: [],
            pollOptions: [],
          },
        })
      }
      throw new Error(`Unexpected GET ${path}`)
    })
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-10T12:00:00.000Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('starts the mobile summary disclosure collapsed, showing a windows-count line_S4', async () => {
    mockCalendar({ blockOccurrence: true })
    renderPage()

    // The Delete affordance in the always-visible `lg`+ copy only appears
    // once the calendar query resolves — waiting on it doubles as waiting
    // for the collapsed count line below to reflect real data.
    await screen.findByRole('button', { name: /Delete/ })

    const toggle = screen.getByRole('button', { name: /Your availability/ })
    expect(toggle).toHaveAttribute('aria-expanded', 'false')
    expect(toggle).toHaveTextContent('1 window')
    // One Delete affordance already exists (the always-visible `lg`+ copy)
    // even while the mobile copy is collapsed — expanding adds a second one.
    expect(screen.getAllByRole('button', { name: /Delete/ })).toHaveLength(1)
  })

  it('expands the disclosure to mount a second (mobile) copy of the summary and drops the count line_S4', async () => {
    mockCalendar({ blockOccurrence: true })
    renderPage()

    await screen.findByRole('button', { name: /Delete/ })
    const toggle = screen.getByRole('button', { name: /Your availability/ })
    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-expanded', 'true')
    expect(toggle).not.toHaveTextContent('window')
    expect(screen.getAllByRole('button', { name: /Delete/ })).toHaveLength(2)
  })

  it('places the revealed panel after its trigger in DOM order and wires aria-controls_F1', async () => {
    mockCalendar({ blockOccurrence: true })
    renderPage()

    await screen.findByRole('button', { name: /Delete/ })
    const toggle = screen.getByRole('button', { name: /Your availability/ })
    fireEvent.click(toggle)

    const panelId = toggle.getAttribute('aria-controls')
    expect(panelId).toBeTruthy()
    const panel = document.getElementById(panelId!)
    expect(panel).not.toBeNull()

    // Bit 4 (DOCUMENT_POSITION_FOLLOWING) means `panel` comes after `toggle`
    // in document order — a forward Tab from the button must land inside it.
    const position = toggle.compareDocumentPosition(panel!)
    expect(position & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  })

  it('announces the disclosure action verb via an sr-only span that flips with state_F2', async () => {
    mockCalendar({ blockOccurrence: true })
    renderPage()

    await screen.findByRole('button', { name: /Delete/ })
    const collapsedToggle = screen.getByRole('button', { name: /Show your availability/ })
    expect(collapsedToggle).toBeInTheDocument()

    fireEvent.click(collapsedToggle)

    const expandedToggle = screen.getByRole('button', { name: /Hide your availability/ })
    expect(expandedToggle).toBeInTheDocument()
  })

  it('hosts both the summary and the legend in the aside on the agenda route too_S4', async () => {
    mockCalendar({
      series: [
        {
          seriesId: 'series-1',
          dayOfWeek: 2,
          startTime: '20:00',
          durationMinutes: 60,
          zoneId: 'UTC',
          effectiveFrom: '2026-08-01',
          effectiveUntil: null,
        },
      ],
    })
    renderPage(['/calendar/agenda'])

    expect(await screen.findByRole('button', { name: /Your availability/ })).toBeInTheDocument()
    // The legend (`CalendarLayers`) keeps its layer control on the agenda
    // route (D-111/D-135) — it was never removed, only relocated into the
    // aside alongside the summary.
    expect(screen.getByText('Layers')).toBeInTheDocument()
    expect(screen.getByLabelText('My availability')).toBeChecked()
  })
})

// CALENDAR_DESIGN.md §9 S5: below `sm` the grid shows a three-day window into
// the displayed week. The window is an index into that week — it never changes
// which week is loaded except through the pager's own week rolls.
describe('WeekGrid §9 S5 day window', () => {
  const windowedGridProps = {
    dayLabels,
    endHour: 11,
    merged: [] as GridRange[],
    occurrences: [] as GridOccurrence[],
    startHour: 9,
    timeLabelFormatter,
    weekStart: localDate(0),
  }

  function windowedColumnLabels() {
    return screen
      .getAllByRole('columnheader')
      .slice(1)
      .map((header) => header.querySelector('span')?.textContent)
  }

  it('renders only the windowed columns, with window-relative ARIA_S5', () => {
    renderGrid({ dayWindow: { start: 2, length: 3 } })

    expect(screen.getByRole('grid')).toHaveAttribute('aria-colcount', '4')
    expect(windowedColumnLabels()).toEqual(['Wed', 'Thu', 'Fri'])

    // `aria-colindex` counts from the rowheader inside the window, so the
    // weekday it can no longer identify is carried by the cell's own name.
    expect(screen.getByRole('gridcell', { name: 'Wed 9:00' })).toHaveAttribute(
      'aria-colindex',
      '2',
    )
    expect(screen.getByRole('gridcell', { name: 'Fri 9:00' })).toHaveAttribute(
      'aria-colindex',
      '4',
    )
    expect(screen.queryByRole('gridcell', { name: 'Tue 9:00' })).not.toBeInTheDocument()
    expect(screen.queryByRole('gridcell', { name: 'Sat 9:00' })).not.toBeInTheDocument()
  })

  // The desktop contract: an explicit full-week window is the same grid the
  // component renders with no window prop at all, node for node.
  it('renders a full-week window identically to no window at all_S5', () => {
    renderGrid({})
    const withoutWindow = screen.getByRole('grid').outerHTML
    cleanup()

    renderGrid({ dayWindow: { start: 0, length: 7 } })
    expect(screen.getByRole('grid').outerHTML).toBe(withoutWindow)
  })

  // A controlled harness: the window state lives in the parent (the page owns
  // it in production), so the edge-navigation fact has to prove the request AND
  // the focus landing after that state settles.
  function WindowedHarness({ initialStart = 2 }: { initialStart?: number }) {
    const [start, setStart] = useState(initialStart)
    return (
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start, length: 3 }}
        onCreateBlock={vi.fn()}
        onDayWindowStartChange={setStart}
        onEditOccurrence={vi.fn()}
      />
    )
  }

  it('moves the window by one day at a window edge and focuses the revealed column_S5', () => {
    render(<WindowedHarness />)

    const leftEdge = screen.getByRole('gridcell', { name: 'Wed 9:00' })
    leftEdge.focus()
    fireEvent.keyDown(leftEdge, { key: 'ArrowLeft' })

    expect(windowedColumnLabels()).toEqual(['Tue', 'Wed', 'Thu'])
    expect(screen.getByRole('gridcell', { name: 'Tue 9:00' })).toHaveFocus()

    const rightEdge = screen.getByRole('gridcell', { name: 'Thu 9:00' })
    fireEvent.keyDown(rightEdge, { key: 'ArrowRight' })

    expect(windowedColumnLabels()).toEqual(['Wed', 'Thu', 'Fri'])
    expect(screen.getByRole('gridcell', { name: 'Fri 9:00' })).toHaveFocus()
  })

  it('navigates inside the window without moving it_S5', () => {
    render(<WindowedHarness />)

    const middle = screen.getByRole('gridcell', { name: 'Thu 9:00' })
    middle.focus()
    fireEvent.keyDown(middle, { key: 'ArrowLeft' })

    expect(windowedColumnLabels()).toEqual(['Wed', 'Thu', 'Fri'])
    expect(screen.getByRole('gridcell', { name: 'Wed 9:00' })).toHaveFocus()
  })

  it('clamps at the week edges instead of wrapping_S5', () => {
    const onDayWindowStartChange = vi.fn<(start: number) => void>()
    const { rerender } = render(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={vi.fn()}
        onDayWindowStartChange={onDayWindowStartChange}
        onEditOccurrence={vi.fn()}
      />,
    )

    const weekStartCell = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    weekStartCell.focus()
    fireEvent.keyDown(weekStartCell, { key: 'ArrowLeft' })
    expect(onDayWindowStartChange).not.toHaveBeenCalled()
    expect(weekStartCell).toHaveFocus()

    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 4, length: 3 }}
        onCreateBlock={vi.fn()}
        onDayWindowStartChange={onDayWindowStartChange}
        onEditOccurrence={vi.fn()}
      />,
    )

    const weekEndCell = screen.getByRole('gridcell', { name: 'Sun 9:00' })
    weekEndCell.focus()
    fireEvent.keyDown(weekEndCell, { key: 'ArrowRight' })
    expect(onDayWindowStartChange).not.toHaveBeenCalled()
    expect(weekEndCell).toHaveFocus()
  })

  it('never moves a full-week window from the keyboard_S5', () => {
    const onDayWindowStartChange = vi.fn<(start: number) => void>()
    render(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 7 }}
        onCreateBlock={vi.fn()}
        onDayWindowStartChange={onDayWindowStartChange}
        onEditOccurrence={vi.fn()}
      />,
    )

    const lastDay = screen.getByRole('gridcell', { name: 'Sun 9:00' })
    lastDay.focus()
    fireEvent.keyDown(lastDay, { key: 'ArrowRight' })

    expect(onDayWindowStartChange).not.toHaveBeenCalled()
    expect(lastDay).toHaveFocus()
  })

  // A pending selection on a day that scrolls out of the window is abandoned —
  // both the rendered band and the commit-time ref, so the next Enter starts a
  // fresh selection instead of committing the invisible one.
  it('clears a selection whose day leaves the window_S5', () => {
    const onCreateBlock = vi.fn<(range: GridRange, anchor: DOMRect) => void>()
    const { rerender } = render(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )

    const start = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })
    expect(document.querySelectorAll('[data-selection-edge]').length).toBeGreaterThan(0)

    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 4, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )

    expect(document.querySelectorAll('[data-selection-edge]')).toHaveLength(0)

    const revealed = screen.getByRole('gridcell', { name: 'Fri 9:00' })
    revealed.focus()
    fireEvent.keyDown(revealed, { key: 'Enter' })

    expect(onCreateBlock).not.toHaveBeenCalled()
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')
  })

  // Walking the window away and back within the *same* week must not
  // resurrect the selection either — the eager clear fires the moment the
  // day leaves the window, so walking back to include that day again finds
  // no selection state left to revive.
  it('does not resurrect a selection walked away and back within the same week_S5', () => {
    const onCreateBlock = vi.fn<(range: GridRange, anchor: DOMRect) => void>()
    const { rerender } = render(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )

    const start = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })
    expect(document.querySelectorAll('[data-selection-edge]').length).toBeGreaterThan(0)

    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 4, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )
    expect(document.querySelectorAll('[data-selection-edge]')).toHaveLength(0)

    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )
    expect(document.querySelectorAll('[data-selection-edge]')).toHaveLength(0)

    const revealed = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    revealed.focus()
    fireEvent.keyDown(revealed, { key: 'Enter' })

    expect(onCreateBlock).not.toHaveBeenCalled()
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')
  })

  // The vanish is announced the same way an explicit Escape cancel is — a
  // viewer relying on the live region must not be left wondering whether
  // their paint is still pending.
  it('announces the vanish as "Selection canceled." when the day leaves the window_S5', () => {
    const { rerender } = render(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={vi.fn()}
        onEditOccurrence={vi.fn()}
      />,
    )

    const start = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })

    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 4, length: 3 }}
        onCreateBlock={vi.fn()}
        onEditOccurrence={vi.fn()}
      />,
    )

    expect(screen.getByRole('status')).toHaveTextContent('Selection canceled.')
  })

  // A day index (0-6) is only meaningful relative to one `weekStart` — the
  // window walking back to include it again after a week roll must not
  // resurrect a selection painted in the previous week, even though the
  // rendered band would look identical (the same day-of-week column).
  it('drops a selection across a week roll instead of committing it 7 days off_S5', () => {
    const onCreateBlock = vi.fn<(range: GridRange, anchor: DOMRect) => void>()
    const rolledWeekStart = new Date(windowedGridProps.weekStart.getTime() + 7 * 24 * 60 * 60 * 1000)
    const { rerender } = render(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )

    const start = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    start.focus()
    fireEvent.keyDown(start, { key: 'Enter' })
    expect(document.querySelectorAll('[data-selection-edge]').length).toBeGreaterThan(0)

    // Roll to the next week (the mobile pager's own week-roll case).
    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
        weekStart={rolledWeekStart}
      />,
    )
    expect(document.querySelectorAll('[data-selection-edge]')).toHaveLength(0)

    // Roll back to the original week — the same day index (0) is visible
    // again, in the same window position.
    rerender(
      <WeekGrid
        {...windowedGridProps}
        dayWindow={{ start: 0, length: 3 }}
        onCreateBlock={onCreateBlock}
        onEditOccurrence={vi.fn()}
      />,
    )
    expect(document.querySelectorAll('[data-selection-edge]')).toHaveLength(0)

    // A single Enter on the reappeared cell must start a fresh selection,
    // never commit the one painted before the roll — a commit here would be
    // the 7-day-off block the fix prevents.
    const revealed = screen.getByRole('gridcell', { name: 'Mon 9:00' })
    revealed.focus()
    fireEvent.keyDown(revealed, { key: 'Enter' })
    expect(onCreateBlock).not.toHaveBeenCalled()
    expect(screen.getByRole('status')).toHaveTextContent('Selection started at 9:00.')

    // The normal two-step flow still commits, using the current week.
    fireEvent.keyDown(revealed, { key: 'Enter' })
    expect(onCreateBlock).toHaveBeenCalledTimes(1)
    const [range] = onCreateBlock.mock.calls[0]
    expect(range.start.getTime()).toBe(
      windowedGridProps.weekStart.getTime() + 9 * 60 * 60 * 1000,
    )
  })
})

// CALENDAR_DESIGN.md §9 S5: the page owns the window state and the pager. The
// fixture week is `en`'s Sunday-first week of 2026-08-09..15, so the day index
// of "today" is what the initial-window rule clamps into [0, 4].
describe('WeekGridPage §9 S5 day pager', () => {
  const originalMatchMedia = window.matchMedia

  function setViewport(isMobile: boolean) {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: vi.fn().mockReturnValue({
        matches: isMobile,
        media: '(max-width: 639px)',
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      }),
    })
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-08-10T12:00:00.000Z'))
    setViewport(true)
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
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: originalMatchMedia,
    })
  })

  /** The visible window as "<weekday> <date>", which pins the week as well as
   * the three days — a window that silently rolled a week would still show the
   * same three weekday names. */
  async function visibleWindow() {
    const headers = await screen.findAllByRole('columnheader', {}, { timeout: 5000 })
    return headers.slice(1).map((header) => {
      const spans = header.querySelectorAll('span')
      return `${spans[0].textContent} ${spans[1].textContent}`
    })
  }

  it('anchors the initial window on today, clamped into the week_S5', async () => {
    const expectations: [string, string[]][] = [
      ['2026-08-09T12:00:00.000Z', ['Sun 9', 'Mon 10', 'Tue 11']],
      ['2026-08-10T12:00:00.000Z', ['Mon 10', 'Tue 11', 'Wed 12']],
      ['2026-08-11T12:00:00.000Z', ['Tue 11', 'Wed 12', 'Thu 13']],
      ['2026-08-12T12:00:00.000Z', ['Wed 12', 'Thu 13', 'Fri 14']],
      ['2026-08-13T12:00:00.000Z', ['Thu 13', 'Fri 14', 'Sat 15']],
      // Friday and Saturday clamp: the window cannot start past day 4 without
      // running off the end of the displayed week.
      ['2026-08-14T12:00:00.000Z', ['Thu 13', 'Fri 14', 'Sat 15']],
      ['2026-08-15T12:00:00.000Z', ['Thu 13', 'Fri 14', 'Sat 15']],
    ]

    for (const [instant, expected] of expectations) {
      vi.setSystemTime(new Date(instant))
      renderPage()
      expect(await visibleWindow()).toEqual(expected)
      cleanup()
    }
  })

  it('starts at the first three days of a week that does not contain today_S5', async () => {
    renderPage()
    expect(await visibleWindow()).toEqual(['Mon 10', 'Tue 11', 'Wed 12'])

    fireEvent.click(screen.getByRole('button', { name: 'Next week' }))

    expect(await visibleWindow()).toEqual(['Sun 16', 'Mon 17', 'Tue 18'])
  })

  it('jumps the window from the day strip, clamped into the week_S5', async () => {
    renderPage()
    await visibleWindow()

    const strip = screen.getByRole('group', { name: 'Days shown' })
    fireEvent.click(within(strip).getByRole('button', { name: 'Show Sat' }))

    expect(await visibleWindow()).toEqual(['Thu 13', 'Fri 14', 'Sat 15'])
    expect(within(strip).getByRole('button', { name: 'Show Sat' })).toHaveAttribute(
      'aria-pressed',
      'true',
    )
    expect(within(strip).getByRole('button', { name: 'Show Sun' })).toHaveAttribute(
      'aria-pressed',
      'false',
    )
  })

  it('marks today in the day strip_S5', async () => {
    renderPage()
    await visibleWindow()

    const strip = screen.getByRole('group', { name: 'Days shown' })
    const todayButton = within(strip).getByRole('button', { name: 'Show Mon, today' })
    expect(todayButton).toHaveAttribute('aria-current', 'date')
    expect(todayButton.className).toMatch(/underline/)
  })

  it('steps the window by a day and rolls the week at its edges_S5', async () => {
    renderPage()
    expect(await visibleWindow()).toEqual(['Mon 10', 'Tue 11', 'Wed 12'])

    const previousDay = screen.getByRole('button', { name: 'Show the earlier day' })
    fireEvent.click(previousDay)
    expect(await visibleWindow()).toEqual(['Sun 9', 'Mon 10', 'Tue 11'])

    // At the week's start the step rolls into the previous week and lands the
    // window at its end, so the move still reads as one day back.
    fireEvent.click(previousDay)
    expect(await visibleWindow()).toEqual(['Thu 6', 'Fri 7', 'Sat 8'])

    fireEvent.click(screen.getByRole('button', { name: 'Show the later day' }))
    expect(await visibleWindow()).toEqual(['Sun 9', 'Mon 10', 'Tue 11'])
  })

  it('returns to the week and window containing today_S5', async () => {
    renderPage()
    await visibleWindow()

    const previousDay = screen.getByRole('button', { name: 'Show the earlier day' })
    fireEvent.click(previousDay)
    fireEvent.click(previousDay)
    expect(await visibleWindow()).toEqual(['Thu 6', 'Fri 7', 'Sat 8'])

    fireEvent.click(screen.getByRole('button', { name: 'Today' }))

    expect(await visibleWindow()).toEqual(['Mon 10', 'Tue 11', 'Wed 12'])
  })

  // The horizontal-scroll fallback is gone, which is what lets the day-header
  // row stick to the page below `sm` — a scroll container captures its own
  // sticky descendants.
  it('keeps the header row sticky below sm with no horizontal scrollport_S5', async () => {
    renderPage()
    await visibleWindow()

    const grid = screen.getByRole('grid')
    expect(grid.className).not.toMatch(/overflow-x-(auto|scroll)/)
    const headerRow = within(grid).getAllByRole('row')[0]
    expect(headerRow.className).toMatch(/sticky/)
    expect(headerRow.className).toMatch(/top-0/)
  })

  it('shows the whole week and no pager at sm and above_S5', async () => {
    setViewport(false)
    renderPage()

    expect(await visibleWindow()).toEqual([
      'Sun 9',
      'Mon 10',
      'Tue 11',
      'Wed 12',
      'Thu 13',
      'Fri 14',
      'Sat 15',
    ])
    expect(screen.getByRole('grid')).toHaveAttribute('aria-colcount', '8')
    expect(screen.queryByTestId('day-pager')).not.toBeInTheDocument()
  })
})
