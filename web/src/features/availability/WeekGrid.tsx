import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

export type GridOccurrence = {
  id: string
  start: Date
  end: Date
}

export type GridRange = {
  start: Date
  end: Date
}

/** A resize/move released on an existing window, in the same fake-local
 * convention as every other Date this component produces. */
export type GridAdjustment = {
  occurrenceId: string
  start: Date
  end: Date
}

export type GridFriendRange = {
  /** Already resolved by the caller from `MaskedParticipant` — a display
   * name or the Anonymous User fallback (§8.1: no id ever crosses here). */
  ownerLabel: string
  start: Date
  end: Date
}

export type GridViewerRelation =
  | 'organizer'
  | 'invitee'
  | 'participant'
  | 'requester'
  | 'inherited'

export type GridSessionEntry = {
  id: string
  title: string
  start: Date
  end: Date
  viewerRelation: GridViewerRelation
}

/** The `pollOptions` layer (D-111/A-080, §5's calendar bullet) — one instant per option,
 * painted onto the single 15-min slot it starts in (an option carries no duration, spec
 * §7.3-1). `ownVote` renders through the same value glyphs the voting grid uses (never
 * color-alone), so a slot's own-vote state is legible without opening the poll. */
export type GridPollOptionEntry = {
  id: string
  pollId: string
  pollTitle: string
  start: Date
  ownVote: 'available' | 'maybe' | 'unavailable' | null
}

export type WeekGridProps = {
  /** The grid's first visible day at local midnight — already in the
   * viewer's profile zone (D-046); WeekGrid itself never converts zones. */
  weekStart: Date
  startHour?: number
  endHour?: number
  occurrences: GridOccurrence[]
  /** The server's merged ranges (D-101 visual half, A-076) — WeekGrid paints
   * from these; it never re-derives a merge from `occurrences`. */
  merged: GridRange[]
  /** The `friendAvailability` layer (A-080) — ranges only, no authoring
   * identity. Rendered as read-only content inside the covered cells, never
   * as its own gridcell or tab stop (§10.3's ARIA audit). */
  friendRanges?: GridFriendRange[]
  /** The `sessions` layer (A-077/A-080), already access-kind `any` — an
   * inherited row (D-149's discoverable sessions) renders with a distinct,
   * non-color-only label, never a second gridcell. */
  sessionEntries?: GridSessionEntry[]
  /** The `pollOptions` layer (D-111/A-080) — instants only, painted into the one slot each
   * starts in; never a second gridcell or tab stop (§10.3's ARIA audit). */
  pollOptionEntries?: GridPollOptionEntry[]
  onCreateBlock: (range: GridRange) => void
  onEditOccurrence: (occurrenceId: string) => void
  /** Omitted when the caller has nothing to do with an adjustment — the edge
   * handles are then not rendered at all, rather than rendered inert. */
  onAdjustOccurrence?: (adjustment: GridAdjustment) => void
  dayLabels: string[]
  timeLabelFormatter: (date: Date) => string
  /** The current wall-clock instant in the same fake-local convention as
   * `weekStart` (CALENDAR_DESIGN.md §9 S2). The caller owns the minute tick
   * and the zone conversion; this component only diffs the value against
   * `weekStart`/`startHour`/`endHour` to place the today tint and the
   * presentational now-line. */
  now?: Date | null
}

const SLOT_MINUTES = 15
const SLOT_MS = SLOT_MINUTES * 60_000
const DAY_MS = 24 * 60 * 60 * 1000
const LONG_PRESS_MS = 400
const MOVE_CANCEL_PX = 10
const AUTO_SCROLL_EDGE_PX = 32
const AUTO_SCROLL_STEP_PX = 12
// A cell is `min-h-11`, so 44px is the floor a slot can occupy. It is also the
// only height available when the measured rect is zero — a grid inside a
// display:none ancestor, or a layout that has not been flushed yet — where
// dividing by 0 would make every drag an infinite slot delta.
const SLOT_HEIGHT_FLOOR_PX = 44

type Cursor = { day: number; slot: number }
type Selection = { day: number; anchorSlot: number; endSlot: number }
type ResizeDrag = {
  occurrenceId: string
  edge: 'start' | 'end'
  originY: number
  slotHeight: number
  start: Date
  end: Date
}

/** Applies a whole-slot delta to one edge, never letting the window collapse
 * past a single slot — a drag that overshoots stops at the minimum rather
 * than inverting the range. */
function resizedRange(drag: ResizeDrag, deltaSlots: number): GridRange {
  const shift = deltaSlots * SLOT_MS
  if (drag.edge === 'end') {
    return {
      start: drag.start,
      end: new Date(Math.max(drag.start.getTime() + SLOT_MS, drag.end.getTime() + shift)),
    }
  }
  return {
    start: new Date(Math.min(drag.end.getTime() - SLOT_MS, drag.start.getTime() + shift)),
    end: drag.end,
  }
}

function slotsPerDay(startHour: number, endHour: number) {
  return ((endHour - startHour) * 60) / SLOT_MINUTES
}

// `weekStart` and every Date this component produces are "fake local": the
// UTC accessors hold the viewer's profile-zone wall clock (D-046). Using
// UTC accessors throughout — never the host's own local getters/setters —
// is what keeps the grid's arithmetic independent of the machine it runs
// on, in both the browser and this file's tests.
function cellTime(weekStart: Date, day: number, slot: number, startHour: number) {
  const date = new Date(weekStart)
  date.setUTCDate(date.getUTCDate() + day)
  date.setUTCHours(startHour, slot * SLOT_MINUTES, 0, 0)
  return date
}

function overlaps(rangeStart: Date, rangeEnd: Date, cellStart: Date, cellEnd: Date) {
  return rangeStart < cellEnd && rangeEnd > cellStart
}

function dayBounds(weekStart: Date, day: number, startHour: number, slots: number) {
  return {
    start: cellTime(weekStart, day, 0, startHour),
    end: cellTime(weekStart, day, slots, startHour),
  }
}

/** The clamped `[startSlot, endSlot]` (inclusive) a range covers within one
 * day's visible slots, plus whether either edge was clamped to the viewport
 * rather than being the entry's true boundary — §8's "clamped edge renders
 * flat" distinction. `null` when the range doesn't touch this day at all. */
function clampedSlotRange(
  entryStart: Date,
  entryEnd: Date,
  dayStart: Date,
  dayEnd: Date,
  slots: number,
): { startSlot: number; endSlot: number; clampedAtStart: boolean; clampedAtEnd: boolean } | null {
  if (!(entryStart < dayEnd && entryEnd > dayStart)) return null
  const clampedStart = entryStart < dayStart ? dayStart : entryStart
  const clampedEndExclusive = entryEnd > dayEnd ? dayEnd : entryEnd
  if (clampedEndExclusive <= clampedStart) return null
  const startSlot = Math.max(0, Math.floor((clampedStart.getTime() - dayStart.getTime()) / SLOT_MS))
  const endSlot = Math.min(
    slots - 1,
    Math.ceil((clampedEndExclusive.getTime() - dayStart.getTime()) / SLOT_MS) - 1,
  )
  return {
    startSlot,
    endSlot,
    clampedAtStart: entryStart < dayStart,
    clampedAtEnd: entryEnd > dayEnd,
  }
}

type OccurrenceCell = {
  occurrence: GridOccurrence
  isStart: boolean
  isEnd: boolean
}

type MergedCell = {
  isStart: boolean
  isEnd: boolean
  clampedAtStart: boolean
  clampedAtEnd: boolean
}

type SessionCell = {
  session: GridSessionEntry
  isStart: boolean
  isEnd: boolean
}

type FriendCell = {
  ranges: GridFriendRange[]
  isSegmentStart: boolean
}

type BandMeta = {
  occurrenceGrid: (OccurrenceCell | undefined)[][]
  mergedGrid: (MergedCell | undefined)[][]
  sessionGrid: SessionCell[][][]
  friendGrid: FriendCell[][]
  pollGrid: GridPollOptionEntry[][][]
}

const DAY_COUNT = 7

// §9 S2 simulates the prototype's per-column cards on the unchanged row-major
// DOM: the inter-column gap is a margin carried by every cell of a column
// (header chip included), never a restructured grid.
const COLUMN_GAP = 'mx-[3px]'

/** §8's band metadata: a per-day indexed lookup replacing the per-cell
 * linear scans over every occurrence/merged/friend/session/poll array, plus
 * start/middle/end span info per entry (visible-range clamped) that the
 * label/border/rounding rendering below reads instead of re-deriving it. */
function computeBandMeta(
  occurrences: GridOccurrence[],
  merged: GridRange[],
  friendRanges: GridFriendRange[],
  sessionEntries: GridSessionEntry[],
  pollOptionEntries: GridPollOptionEntry[],
  weekStart: Date,
  startHour: number,
  slots: number,
): BandMeta {
  const occurrenceGrid: (OccurrenceCell | undefined)[][] = []
  const mergedGrid: (MergedCell | undefined)[][] = []
  const sessionGrid: SessionCell[][][] = []
  const friendGrid: FriendCell[][] = []
  const pollGrid: GridPollOptionEntry[][][] = []

  for (let day = 0; day < DAY_COUNT; day++) {
    const { start: dayStart, end: dayEnd } = dayBounds(weekStart, day, startHour, slots)
    occurrenceGrid[day] = new Array(slots).fill(undefined)
    mergedGrid[day] = new Array(slots).fill(undefined)
    sessionGrid[day] = Array.from({ length: slots }, () => [])
    friendGrid[day] = Array.from({ length: slots }, () => ({ ranges: [], isSegmentStart: false }))
    pollGrid[day] = Array.from({ length: slots }, () => [])

    // First occurrence covering a slot wins — matches the `.find` semantics
    // this replaces (`merges overlapping own blocks visually_D101`).
    for (const occurrence of occurrences) {
      const range = clampedSlotRange(occurrence.start, occurrence.end, dayStart, dayEnd, slots)
      if (!range) continue
      for (let slot = range.startSlot; slot <= range.endSlot; slot++) {
        if (occurrenceGrid[day][slot]) continue
        occurrenceGrid[day][slot] = {
          occurrence,
          isStart: slot === range.startSlot,
          isEnd: slot === range.endSlot,
        }
      }
    }

    for (const range of merged) {
      const r = clampedSlotRange(range.start, range.end, dayStart, dayEnd, slots)
      if (!r) continue
      for (let slot = r.startSlot; slot <= r.endSlot; slot++) {
        mergedGrid[day][slot] = {
          isStart: slot === r.startSlot,
          isEnd: slot === r.endSlot,
          clampedAtStart: r.clampedAtStart,
          clampedAtEnd: r.clampedAtEnd,
        }
      }
    }

    for (const session of sessionEntries) {
      const r = clampedSlotRange(session.start, session.end, dayStart, dayEnd, slots)
      if (!r) continue
      for (let slot = r.startSlot; slot <= r.endSlot; slot++) {
        sessionGrid[day][slot].push({
          session,
          isStart: slot === r.startSlot,
          isEnd: slot === r.endSlot,
        })
      }
    }

    for (const option of pollOptionEntries) {
      if (option.start < dayStart || option.start >= dayEnd) continue
      const slot = Math.min(
        slots - 1,
        Math.max(0, Math.floor((option.start.getTime() - dayStart.getTime()) / SLOT_MS)),
      )
      pollGrid[day][slot].push(option)
    }

    // Friend coverage is a segment, not a per-friend span: the covering set
    // (by reference — `friendRanges` is a stable prop array) is compared
    // slot to slot, and a new segment starts wherever that set changes —
    // including where it becomes empty then non-empty again.
    let previous: GridFriendRange[] = []
    for (let slot = 0; slot < slots; slot++) {
      const cellStart = cellTime(weekStart, day, slot, startHour)
      const cellEnd = cellTime(weekStart, day, slot + 1, startHour)
      const covering = friendRanges.filter((range) =>
        overlaps(range.start, range.end, cellStart, cellEnd),
      )
      const sameAsPrevious =
        covering.length === previous.length &&
        covering.every((range, index) => range === previous[index])
      friendGrid[day][slot] = {
        ranges: covering,
        isSegmentStart: covering.length > 0 && !sameAsPrevious,
      }
      previous = covering
    }
  }

  return { occurrenceGrid, mergedGrid, sessionGrid, friendGrid, pollGrid }
}

export function WeekGrid({
  weekStart,
  startHour = 0,
  endHour = 24,
  occurrences,
  merged,
  friendRanges = [],
  sessionEntries = [],
  pollOptionEntries = [],
  onCreateBlock,
  onEditOccurrence,
  onAdjustOccurrence,
  dayLabels,
  timeLabelFormatter,
  now = null,
}: WeekGridProps) {
  const { t } = useTranslation('availability')
  const slots = slotsPerDay(startHour, endHour)
  // Zone-free by contract (see `weekStart`): "today" and the now-line position
  // are pure differences between the injected fake-local `now` and this grid's
  // own `weekStart`/`startHour` window — never `new Date()` or a zone lookup.
  const todayIndex = useMemo(() => {
    if (!now) return null
    const day = Math.floor((now.getTime() - weekStart.getTime()) / DAY_MS)
    return day >= 0 && day < DAY_COUNT ? day : null
  }, [now, weekStart])
  const nowMarker = useMemo(() => {
    if (!now || todayIndex === null) return null
    const dayStart = cellTime(weekStart, todayIndex, 0, startHour)
    const offset = now.getTime() - dayStart.getTime()
    if (offset < 0 || offset >= slots * SLOT_MS) return null
    return {
      day: todayIndex,
      slot: Math.floor(offset / SLOT_MS),
      fraction: (offset % SLOT_MS) / SLOT_MS,
    }
  }, [now, todayIndex, weekStart, startHour, slots])
  const bandMeta = useMemo(
    () =>
      computeBandMeta(
        occurrences,
        merged,
        friendRanges,
        sessionEntries,
        pollOptionEntries,
        weekStart,
        startHour,
        slots,
      ),
    [occurrences, merged, friendRanges, sessionEntries, pollOptionEntries, weekStart, startHour, slots],
  )
  const [focused, setFocused] = useState<Cursor>({ day: 0, slot: 0 })
  const [selection, setSelection] = useState<Selection | null>(null)
  const [liveMessage, setLiveMessage] = useState('')
  // Mirrors `dragging.current` as render state, for exactly one purpose:
  // gating `touch-action` on the grid container. Before a long-press fires,
  // touch-action stays at its default so an ordinary touch-and-drag still
  // scrolls the page natively — only once a press has been held long enough
  // to become a selection does the container opt out of native panning, so
  // the browser's own scroll gesture stops fighting the drag-extend
  // handlers (§10.3's `touch-action:none` conflict, P5-TOUCH-01).
  const [isTouchDragging, setIsTouchDragging] = useState(false)
  // Edge handles belong to one window at a time: permanent handles on every
  // window would turn each of them into a scroll trap on touch (they must set
  // `touch-action: none` on themselves to be draggable at all).
  const [selectedOccurrenceId, setSelectedOccurrenceId] = useState<string | null>(null)
  const [resizePreview, setResizePreview] = useState<GridRange | null>(null)
  const resizeDrag = useRef<ResizeDrag | null>(null)
  // Cancelling `pointerdown` suppresses only the compatibility mouse events;
  // the `click` still fires on the handle and bubbles to the gridcell. A
  // resize that moved must not also be read as a tap on the block.
  const resizeMoved = useRef(false)
  const dragging = useRef(false)
  const longPressTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const touchOrigin = useRef<{ x: number; y: number } | null>(null)
  const autoScrollFrame = useRef<number | undefined>(undefined)
  const cellRefs = useRef(new Map<string, HTMLDivElement>())
  // Roving tabindex only steals DOM focus once the grid has already been
  // entered (Tab or a click) — the initial mount must not yank focus away
  // from the rest of the page.
  const hasInteracted = useRef(false)

  useEffect(() => {
    if (!hasInteracted.current) return
    cellRefs.current.get(`${focused.day}:${focused.slot}`)?.focus()
  }, [focused])

  useEffect(
    () => () => {
      if (longPressTimer.current) clearTimeout(longPressTimer.current)
      if (autoScrollFrame.current) cancelAnimationFrame(autoScrollFrame.current)
    },
    [],
  )

  function occurrenceAt(day: number, slot: number) {
    return bandMeta.occurrenceGrid[day][slot]?.occurrence
  }

  function mergedAt(day: number, slot: number) {
    return bandMeta.mergedGrid[day][slot] !== undefined
  }

  function friendsAt(day: number, slot: number) {
    return bandMeta.friendGrid[day][slot].ranges
  }

  function sessionsAt(day: number, slot: number) {
    return bandMeta.sessionGrid[day][slot].map((cell) => cell.session)
  }

  function pollOptionsAt(day: number, slot: number) {
    return bandMeta.pollGrid[day][slot]
  }

  // §8: the resize handles occupy the full cell of the selected occurrence's
  // start/end slot — a session/friend label rendering underneath there would
  // be occluded. `bandActiveAt` mirrors the fill's own busy||resizing test
  // (WeekGrid.tsx:677-681) so the band border/rounding below can never
  // disagree with what's painted.
  function handleOccupiesCell(day: number, slot: number) {
    if (!onAdjustOccurrence || !selectedOccurrenceId) return false
    const cell = bandMeta.occurrenceGrid[day][slot]
    if (!cell || cell.occurrence.id !== selectedOccurrenceId) return false
    return cell.isStart || cell.isEnd
  }

  function bandActiveAt(day: number, slot: number) {
    if (bandMeta.mergedGrid[day][slot]) return true
    const cell = bandMeta.occurrenceGrid[day][slot]
    if (!cell || !resizePreview || cell.occurrence.id !== selectedOccurrenceId) return false
    const cellStart = cellTime(weekStart, day, slot, startHour)
    const cellEnd = cellTime(weekStart, day, slot + 1, startHour)
    return overlaps(resizePreview.start, resizePreview.end, cellStart, cellEnd)
  }

  // Session titles/friend glyphs render once at their band's start slot; a
  // start slot occluded by a resize handle shifts the label one slot later
  // instead of hiding it, but only when the band has a second slot to move
  // into — a single-slot band just accepts the occlusion (§8).
  function sessionLabelsAt(day: number, slot: number): GridSessionEntry[] {
    const result: GridSessionEntry[] = []
    for (const cell of bandMeta.sessionGrid[day][slot]) {
      if (!cell.isStart) continue
      if (!cell.isEnd && handleOccupiesCell(day, slot)) continue
      result.push(cell.session)
    }
    if (slot > 0) {
      for (const cell of bandMeta.sessionGrid[day][slot - 1]) {
        if (cell.isStart && !cell.isEnd && handleOccupiesCell(day, slot - 1)) {
          result.push(cell.session)
        }
      }
    }
    return result
  }

  function friendGlyphRangesAt(day: number, slot: number): GridFriendRange[] {
    const cell = bandMeta.friendGrid[day][slot]
    if (cell.isSegmentStart) {
      const next = slot + 1 < slots ? bandMeta.friendGrid[day][slot + 1] : null
      const segmentContinues = Boolean(next && !next.isSegmentStart && next.ranges.length > 0)
      if (segmentContinues && handleOccupiesCell(day, slot)) return []
      return cell.ranges
    }
    if (slot > 0) {
      const prev = bandMeta.friendGrid[day][slot - 1]
      if (
        prev.isSegmentStart &&
        handleOccupiesCell(day, slot - 1) &&
        cell.ranges.length === prev.ranges.length &&
        cell.ranges.every((range, index) => range === prev.ranges[index])
      ) {
        return cell.ranges
      }
    }
    return []
  }

  function announceExtent(day: number, anchorSlot: number, endSlot: number) {
    const lo = Math.min(anchorSlot, endSlot)
    const hi = Math.max(anchorSlot, endSlot)
    const start = cellTime(weekStart, day, lo, startHour)
    const end = cellTime(weekStart, day, hi + 1, startHour)
    setLiveMessage(
      t('a11y.selectionExtent', {
        start: timeLabelFormatter(start),
        end: timeLabelFormatter(end),
      }),
    )
  }

  function beginSelection(day: number, slot: number) {
    setSelection({ day, anchorSlot: slot, endSlot: slot })
    setLiveMessage(
      t('a11y.selectionStarted', {
        time: timeLabelFormatter(cellTime(weekStart, day, slot, startHour)),
      }),
    )
  }

  function extendSelection(day: number, slot: number) {
    setSelection((current) => {
      if (!current || current.day !== day) return current
      return { ...current, endSlot: slot }
    })
    if (selection) announceExtent(day, selection.anchorSlot, slot)
  }

  function commitSelection(current: Selection) {
    const lo = Math.min(current.anchorSlot, current.endSlot)
    const hi = Math.max(current.anchorSlot, current.endSlot)
    const start = cellTime(weekStart, current.day, lo, startHour)
    const end = cellTime(weekStart, current.day, hi + 1, startHour)
    onCreateBlock({ start, end })
    setLiveMessage(
      t('a11y.selectionReady', {
        start: timeLabelFormatter(start),
        end: timeLabelFormatter(end),
      }),
    )
    setSelection(null)
  }

  function cancelSelection() {
    setSelection(null)
    setLiveMessage(t('a11y.selectionCanceled'))
  }

  function announceRange(range: GridRange) {
    setLiveMessage(
      t('a11y.selectionExtent', {
        start: timeLabelFormatter(range.start),
        end: timeLabelFormatter(range.end),
      }),
    )
  }

  function slotDelta(drag: ResizeDrag, clientY: number) {
    if (!Number.isFinite(clientY)) return 0
    return Math.round((clientY - drag.originY) / drag.slotHeight)
  }

  function beginResize(
    event: React.PointerEvent<HTMLButtonElement>,
    occurrence: GridOccurrence,
    edge: 'start' | 'end',
  ) {
    // Without this the press reaches the cell underneath and is read as the
    // start of a paint (mouse) or a long-press (touch).
    event.stopPropagation()
    event.preventDefault()
    resizeMoved.current = false
    const measured = event.currentTarget.parentElement?.getBoundingClientRect().height ?? 0
    resizeDrag.current = {
      occurrenceId: occurrence.id,
      edge,
      originY: event.clientY,
      slotHeight: measured > 0 ? measured : SLOT_HEIGHT_FLOOR_PX,
      start: occurrence.start,
      end: occurrence.end,
    }
    setResizePreview({ start: occurrence.start, end: occurrence.end })
    event.currentTarget.setPointerCapture?.(event.pointerId)
  }

  function handleResizeMove(event: React.PointerEvent<HTMLButtonElement>) {
    const drag = resizeDrag.current
    if (!drag) return
    event.stopPropagation()
    const next = resizedRange(drag, slotDelta(drag, event.clientY))
    setResizePreview(next)
    announceRange(next)
  }

  function handleResizeUp(event: React.PointerEvent<HTMLButtonElement>) {
    const drag = resizeDrag.current
    if (!drag) return
    event.stopPropagation()
    resizeDrag.current = null
    setResizePreview(null)
    event.currentTarget.releasePointerCapture?.(event.pointerId)
    const delta = slotDelta(drag, event.clientY)
    // A press that never travelled a slot is a tap, not a resize: it must not
    // fire a no-op adjustment, and its click is left to bubble to the cell so
    // a block too short to expose any bare body still opens its editor.
    if (delta === 0) return
    resizeMoved.current = true
    const next = resizedRange(drag, delta)
    announceRange(next)
    onAdjustOccurrence?.({ occurrenceId: drag.occurrenceId, ...next })
  }

  function handleResizeCancel(event: React.PointerEvent<HTMLButtonElement>) {
    if (!resizeDrag.current) return
    event.stopPropagation()
    resizeDrag.current = null
    resizeMoved.current = false
    setResizePreview(null)
  }

  // The pointer gesture's keyboard equivalent (WCAG 2.1.1): one slot per
  // press, committed immediately, so the edge is adjustable without a pointer
  // and without reopening the editor to retype a time.
  function handleResizeKeyDown(
    event: React.KeyboardEvent<HTMLButtonElement>,
    occurrence: GridOccurrence,
    edge: 'start' | 'end',
  ) {
    // A native `<button>`'s Enter/Space would otherwise synthesize a click
    // that bubbles to the cell's `onClick` and re-runs activation on top of
    // whichever branch below already handled the key (§8).
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault()
      event.stopPropagation()
      return
    }
    if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return
    event.preventDefault()
    // The cell below owns ArrowUp/ArrowDown for roving focus; letting this
    // bubble would move focus off the handle mid-adjustment.
    event.stopPropagation()
    const next = resizedRange(
      {
        occurrenceId: occurrence.id,
        edge,
        originY: 0,
        slotHeight: SLOT_HEIGHT_FLOOR_PX,
        start: occurrence.start,
        end: occurrence.end,
      },
      event.key === 'ArrowDown' ? 1 : -1,
    )
    announceRange(next)
    onAdjustOccurrence?.({ occurrenceId: occurrence.id, ...next })
  }

  function handleKeyDown(event: React.KeyboardEvent, day: number, slot: number) {
    switch (event.key) {
      case 'ArrowUp': {
        event.preventDefault()
        const nextSlot = Math.max(0, slot - 1)
        if (event.shiftKey && selection && selection.day === day) {
          extendSelection(day, nextSlot)
        }
        setFocused({ day, slot: nextSlot })
        break
      }
      case 'ArrowDown': {
        event.preventDefault()
        const nextSlot = Math.min(slots - 1, slot + 1)
        if (event.shiftKey && selection && selection.day === day) {
          extendSelection(day, nextSlot)
        }
        setFocused({ day, slot: nextSlot })
        break
      }
      case 'ArrowLeft': {
        event.preventDefault()
        setFocused({ day: Math.max(0, day - 1), slot })
        break
      }
      case 'ArrowRight': {
        event.preventDefault()
        setFocused({ day: Math.min(6, day + 1), slot })
        break
      }
      // §8: Space mirrors Enter exactly — its behavior is state-dependent
      // (start selection / commit / open editor) — with `preventDefault` so
      // the page never scrolls, the ARIA grid pattern's expectation.
      case 'Enter':
      case ' ': {
        event.preventDefault()
        if (selection) {
          commitSelection(selection)
          break
        }
        const occurrence = occurrenceAt(day, slot)
        if (occurrence) {
          setSelectedOccurrenceId(occurrence.id)
          onEditOccurrence(occurrence.id)
        } else {
          setSelectedOccurrenceId(null)
          beginSelection(day, slot)
        }
        break
      }
      case 'Escape': {
        if (selection) {
          event.preventDefault()
          cancelSelection()
        }
        break
      }
      default:
        break
    }
  }

  function stopAutoScroll() {
    if (autoScrollFrame.current) {
      cancelAnimationFrame(autoScrollFrame.current)
      autoScrollFrame.current = undefined
    }
  }

  // The grid has no scrollport of its own — it renders at natural height and
  // the document scrolls — so a drag that runs past the fold has to move the
  // page. The trigger bands are therefore the viewport's own top and bottom
  // edges, not any element rect.
  function maybeAutoScroll(clientY: number) {
    const distanceFromTop = clientY
    const distanceFromBottom = window.innerHeight - clientY

    stopAutoScroll()
    if (distanceFromTop >= 0 && distanceFromTop < AUTO_SCROLL_EDGE_PX) {
      const step = () => {
        window.scrollBy(0, -AUTO_SCROLL_STEP_PX)
        autoScrollFrame.current = requestAnimationFrame(step)
      }
      autoScrollFrame.current = requestAnimationFrame(step)
    } else if (distanceFromBottom >= 0 && distanceFromBottom < AUTO_SCROLL_EDGE_PX) {
      const step = () => {
        window.scrollBy(0, AUTO_SCROLL_STEP_PX)
        autoScrollFrame.current = requestAnimationFrame(step)
      }
      autoScrollFrame.current = requestAnimationFrame(step)
    }
  }

  function handleMouseDown(event: React.MouseEvent, day: number, slot: number) {
    if (event.button !== 0) return
    if (occurrenceAt(day, slot)) return
    dragging.current = true
    setSelectedOccurrenceId(null)
    setFocused({ day, slot })
    beginSelection(day, slot)
  }

  function handleMouseEnter(day: number, slot: number) {
    if (!dragging.current) return
    setFocused({ day, slot })
    extendSelection(day, slot)
  }

  function handleMouseUp() {
    if (!dragging.current) return
    dragging.current = false
    setSelection((current) => {
      if (current) commitSelection(current)
      return null
    })
  }

  // A native `click` only fires when the same element received both
  // mousedown and mouseup — a drag that starts on an empty cell and is
  // released over a block (or vice-versa) never reaches here, so this can't
  // also be misread as "commit the paint selection". `handleMouseDown`
  // already bails on an occurrence (`:325`-ish above) without starting a
  // drag, so a plain click on a block is otherwise inert — this is the only
  // place that opens it.
  function handleClick(day: number, slot: number) {
    if (resizeMoved.current) {
      resizeMoved.current = false
      return
    }
    const occurrence = occurrenceAt(day, slot)
    setSelectedOccurrenceId(occurrence?.id ?? null)
    if (occurrence) onEditOccurrence(occurrence.id)
  }

  // Touch is a capability check (`PointerEvent` + `pointerType`), never UA
  // sniffing (design §10.3): a short tap on an existing block opens its
  // editor, a short tap on empty space does nothing, a long-press on empty
  // space begins a paint selection, and a press that turns into a scroll
  // (movement past the threshold before the timer fires, or before liftoff)
  // commits nothing (A-018/P5-TOUCH-01).
  function handlePointerDown(event: React.PointerEvent, day: number, slot: number) {
    if (event.pointerType !== 'touch') return
    touchOrigin.current = { x: event.clientX, y: event.clientY }
    // A touch that starts on an existing block never becomes a paint drag —
    // the long-press timer simply never starts — but `touchOrigin` still
    // needs setting so `handlePointerUp` can recognise the tap.
    if (occurrenceAt(day, slot)) return
    longPressTimer.current = setTimeout(() => {
      dragging.current = true
      setIsTouchDragging(true)
      setSelectedOccurrenceId(null)
      setFocused({ day, slot })
      beginSelection(day, slot)
    }, LONG_PRESS_MS)
  }

  function handlePointerMove(event: React.PointerEvent, day: number, slot: number) {
    if (event.pointerType !== 'touch') return
    if (dragging.current) {
      maybeAutoScroll(event.clientY)
      setFocused({ day, slot })
      extendSelection(day, slot)
      return
    }
    const origin = touchOrigin.current
    if (!origin) return
    const movedPx = Math.hypot(event.clientX - origin.x, event.clientY - origin.y)
    if (movedPx > MOVE_CANCEL_PX) {
      if (longPressTimer.current) {
        clearTimeout(longPressTimer.current)
        longPressTimer.current = undefined
      }
      // Nulling this beyond the cancel threshold is also what disqualifies
      // the eventual pointerup from being read as a tap.
      touchOrigin.current = null
    }
  }

  function handlePointerUp(event: React.PointerEvent, day: number, slot: number) {
    if (event.pointerType !== 'touch') return
    const wasLongPress = dragging.current
    // A tap: liftoff happened before a long-press fired, and without moving
    // past the cancel threshold in between (which would have nulled this).
    const isTap = !wasLongPress && touchOrigin.current !== null
    if (longPressTimer.current) {
      clearTimeout(longPressTimer.current)
      longPressTimer.current = undefined
    }
    touchOrigin.current = null
    stopAutoScroll()
    if (wasLongPress) {
      dragging.current = false
      setIsTouchDragging(false)
      setSelection((current) => {
        if (current) commitSelection(current)
        return null
      })
      return
    }
    if (isTap) {
      const occurrence = occurrenceAt(day, slot)
      setSelectedOccurrenceId(occurrence?.id ?? null)
      if (occurrence) {
        setFocused({ day, slot })
        onEditOccurrence(occurrence.id)
      }
    }
  }

  // The browser reclaims the pointer (scroll takeover, alert, tab switch)
  // with pointercancel, not pointerup — without this the grid would keep
  // touch-none and data-touch-dragging until the next tap. Abandon the
  // selection: a reclaimed gesture commits nothing.
  function handlePointerCancel(event: React.PointerEvent) {
    if (event.pointerType !== 'touch') return
    if (longPressTimer.current) {
      clearTimeout(longPressTimer.current)
      longPressTimer.current = undefined
    }
    touchOrigin.current = null
    stopAutoScroll()
    if (dragging.current) {
      dragging.current = false
      setIsTouchDragging(false)
      setSelection(null)
    }
  }

  const slotIndexes = Array.from({ length: slots }, (_, index) => index)
  const dayIndexes = [0, 1, 2, 3, 4, 5, 6]

  return (
    <div className="surface p-2 sm:p-3">
      {/* The grid has no scrollport of its own: it renders at natural height and
          the document scrolls (nested vertical scrolling is what this replaces).
          Horizontal scrolling survives only below `sm`, where seven 44px columns
          plus the time gutter cannot fit a 360px viewport — that narrow case is
          also the one where the day-header row cannot stick to the page, since a
          scroll container captures its sticky descendants. */}
      <div
        aria-colcount={8}
        aria-label={t('a11y.gridLabel')}
        aria-rowcount={slots + 1}
        className={`max-sm:overflow-x-auto max-sm:[-webkit-overflow-scrolling:touch] ${isTouchDragging ? 'touch-none' : ''}`}
        data-touch-dragging={isTouchDragging}
        role="grid"
        onMouseUp={handleMouseUp}
      >
        {/* §9 S2: a translucent blurred strip carrying one small card chip per
            day. The sticky mechanism itself is unchanged. */}
        <div className="sticky top-0 z-20 flex bg-page/90 pb-1 backdrop-blur" role="row">
          <div
            aria-colindex={1}
            className="sticky left-0 z-10 min-h-11 w-14 shrink-0 bg-page/90"
            role="columnheader"
          />
          {dayIndexes.map((day) => (
            <div
              key={day}
              aria-colindex={day + 2}
              className={`${COLUMN_GAP} flex min-h-11 min-w-11 flex-1 flex-col items-center justify-center rounded-xl border px-1 py-1 text-center ${
                day === todayIndex
                  ? 'border-primary/60 bg-primary/15'
                  : 'border-ink/15 bg-card'
              }`}
              role="columnheader"
            >
              <span className="font-display text-[13px] font-bold leading-tight text-ink">
                {dayLabels[day]}
              </span>
              <span
                aria-hidden="true"
                className="text-[11px] leading-tight tabular-nums text-ink/80"
              >
                {cellTime(weekStart, day, 0, 0).getUTCDate()}
              </span>
            </div>
          ))}
        </div>
        {slotIndexes.map((slot) => (
          <div key={slot} className="flex" role="row">
            <div
              aria-colindex={1}
              aria-rowindex={slot + 2}
              className="sticky left-0 z-10 min-h-11 w-14 shrink-0 bg-card pr-2 text-right font-display text-[11px] font-semibold tabular-nums text-ink-soft"
              role="rowheader"
            >
              {slot % 4 === 0
                ? timeLabelFormatter(cellTime(weekStart, 0, slot, startHour))
                : ''}
            </div>
            {dayIndexes.map((day) => {
              const occurrence = occurrenceAt(day, slot)
              const busy = mergedAt(day, slot)
              const cellFriends = friendsAt(day, slot)
              const glyphFriends = friendGlyphRangesAt(day, slot)
              const cellSessions = sessionsAt(day, slot)
              const cellPollOptions = pollOptionsAt(day, slot)
              const inSelection =
                selection &&
                selection.day === day &&
                slot >= Math.min(selection.anchorSlot, selection.endSlot) &&
                slot <= Math.max(selection.anchorSlot, selection.endSlot)
              const isFocused = focused.day === day && focused.slot === slot
              const key = `${day}:${slot}`
              const cellStart = cellTime(weekStart, day, slot, startHour)
              const cellEnd = cellTime(weekStart, day, slot + 1, startHour)
              const resizing =
                occurrence?.id === selectedOccurrenceId &&
                resizePreview !== null &&
                overlaps(resizePreview.start, resizePreview.end, cellStart, cellEnd)
              const state = inSelection || resizing ? 'selected' : busy ? 'busy' : 'free'

              // Handles sit on the two slots the window actually ends in, so
              // they land on the visible edge rather than floating over the
              // middle of a multi-hour band. A window that starts before or
              // ends after the clamped viewport keeps its handle on the first
              // or last visible slot, where the viewer can still reach it.
              // They must stay inside their own cell's box: the handle carries
              // `touch-none`, so any overhang would kill page scrolling on a
              // neighbouring free cell (P5-TOUCH-01).
              const selectedHere =
                onAdjustOccurrence && occurrence && occurrence.id === selectedOccurrenceId
                  ? occurrence
                  : null
              const occurrenceCell = bandMeta.occurrenceGrid[day][slot]
              const isStartSlot = selectedHere !== null && occurrenceCell?.isStart === true
              const isEndSlot = selectedHere !== null && occurrenceCell?.isEnd === true

              // §8: the busy/selected fill forms a band across contiguous
              // slots — the internal 15-minute border between two cells of
              // the same band is suppressed, the 60-minute line keeps a
              // stronger token than the 15-minute line, and band ends round
              // off unless the true edge is clamped outside the viewport
              // (continues past it, so the visible edge stays flat).
              const bandActive = busy || resizing
              const isRunStart = bandActive && (slot === 0 || !bandActiveAt(day, slot - 1))
              const isRunEnd = bandActive && (slot === slots - 1 || !bandActiveAt(day, slot + 1))
              const mergedCell = bandMeta.mergedGrid[day][slot]
              const isHourLine = slot % 4 === 0
              const topBorderClass =
                bandActive && !isRunStart
                  ? 'border-t-0'
                  : isHourLine
                    ? 'border-t border-t-grid-hour'
                    : 'border-t border-t-grid-line'
              const roundedTop = isRunStart && !mergedCell?.clampedAtStart
              const roundedBottom = isRunEnd && !mergedCell?.clampedAtEnd
              // The column-card corners are the first/last row's own corners;
              // a band edge landing there keeps its own (tighter) radius so
              // the band read never disappears into the card outline.
              const roundingClass = [
                roundedTop ? 'rounded-t-lg' : slot === 0 ? 'rounded-t-2xl' : '',
                roundedBottom ? 'rounded-b-lg' : slot === slots - 1 ? 'rounded-b-2xl' : '',
              ]
                .filter(Boolean)
                .join(' ')
              const bottomBorderClass = slot === slots - 1 ? 'border-b border-b-grid-hour' : ''
              const isTodayColumn = day === todayIndex
              const columnEdgeClass = isTodayColumn
                ? 'border-x-primary/40'
                : 'border-x-grid-hour'

              // §3: the selection ring is drawn around the *band*, not per
              // cell. A drag/keyboard selection is always one column wide
              // (a single day), so the left/right band edges coincide with
              // every selected cell's own edges; only the top and bottom
              // band edges depend on position within the run.
              let selectionEdges = ''
              if (inSelection && selection) {
                const lo = Math.min(selection.anchorSlot, selection.endSlot)
                const hi = Math.max(selection.anchorSlot, selection.endSlot)
                const edges = ['left', 'right']
                if (slot === lo) edges.push('top')
                if (slot === hi) edges.push('bottom')
                selectionEdges = edges.join(' ')
              }

              // Every layer contributes to one accessible name per cell —
              // never a color-only signal, and never a second gridcell for
              // an overlay (§10.3).
              const labelParts = [timeLabelFormatter(cellTime(weekStart, day, slot, startHour))]
              if (busy) labelParts.push(t('a11y.busy'))
              if (cellFriends.length === 1) {
                labelParts.push(t('a11y.friendAvailable', { name: cellFriends[0].ownerLabel }))
              } else if (cellFriends.length > 1) {
                labelParts.push(t('a11y.friendsAvailable', { count: cellFriends.length }))
              }
              for (const session of cellSessions) {
                labelParts.push(
                  session.viewerRelation === 'inherited'
                    ? t('a11y.sessionInherited', { title: session.title })
                    : t('a11y.sessionLabel', { title: session.title }),
                )
              }
              for (const option of cellPollOptions) {
                labelParts.push(
                  option.ownVote
                    ? t('a11y.pollOptionWithVote', {
                        title: option.pollTitle,
                        vote: t(`a11y.pollVote.${option.ownVote}`),
                      })
                    : t('a11y.pollOption', { title: option.pollTitle }),
                )
              }

              return (
                <div
                  key={key}
                  ref={(node) => {
                    if (node) cellRefs.current.set(key, node)
                    else cellRefs.current.delete(key)
                  }}
                  aria-colindex={day + 2}
                  aria-label={labelParts.join(' – ')}
                  aria-rowindex={slot + 2}
                  className={`relative isolate ${COLUMN_GAP} min-h-11 min-w-11 flex-1 border-x ${columnEdgeClass} ${topBorderClass} ${bottomBorderClass} ${roundingClass} bg-free data-[busy=true]:bg-busy focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus data-[state=selected]:before:absolute data-[state=selected]:before:inset-0 data-[state=selected]:before:z-[-1] data-[state=selected]:before:bg-ink/6 data-[state=selected]:before:content-[''] data-[selection-edge~=top]:border-t-2 data-[selection-edge~=top]:border-t-ink data-[selection-edge~=bottom]:border-b-2 data-[selection-edge~=bottom]:border-b-ink data-[selection-edge~=left]:border-l-2 data-[selection-edge~=left]:border-l-ink data-[selection-edge~=right]:border-r-2 data-[selection-edge~=right]:border-r-ink`}
                  data-busy={busy || undefined}
                  data-selection-edge={selectionEdges || undefined}
                  data-state={state}
                  role="gridcell"
                  tabIndex={isFocused ? 0 : -1}
                  onFocus={() => {
                    hasInteracted.current = true
                    // Guarded so the imperative re-focus this triggers
                    // below (via the `focused` effect) doesn't loop: a
                    // same-cell focus event is the effect's own doing, not
                    // a new navigation.
                    setFocused((current) =>
                      current.day === day && current.slot === slot
                        ? current
                        : { day, slot },
                    )
                  }}
                  onClick={() => handleClick(day, slot)}
                  onKeyDown={(event) => handleKeyDown(event, day, slot)}
                  onMouseDown={(event) => handleMouseDown(event, day, slot)}
                  onMouseEnter={() => handleMouseEnter(day, slot)}
                  onPointerDown={(event) => handlePointerDown(event, day, slot)}
                  onPointerMove={(event) => handlePointerMove(event, day, slot)}
                  onPointerCancel={handlePointerCancel}
                  onPointerUp={(event) => handlePointerUp(event, day, slot)}
                >
                  {selectedHere && isStartSlot && (
                    <button
                      aria-label={t('grid.resizeStart')}
                      className="absolute inset-x-0 top-0 z-10 flex h-11 touch-none cursor-ns-resize items-start justify-center bg-transparent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                      type="button"
                      onKeyDown={(event) => handleResizeKeyDown(event, selectedHere, 'start')}
                      onPointerCancel={handleResizeCancel}
                      onPointerDown={(event) => beginResize(event, selectedHere, 'start')}
                      onPointerMove={handleResizeMove}
                      onPointerUp={handleResizeUp}
                    >
                      <span aria-hidden="true" className="block h-1 w-6 rounded-full bg-ink" />
                    </button>
                  )}
                  {selectedHere && isEndSlot && (
                    <button
                      aria-label={t('grid.resizeEnd')}
                      className="absolute inset-x-0 bottom-0 z-10 flex h-11 touch-none cursor-ns-resize items-end justify-center bg-transparent focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
                      type="button"
                      onKeyDown={(event) => handleResizeKeyDown(event, selectedHere, 'end')}
                      onPointerCancel={handleResizeCancel}
                      onPointerDown={(event) => beginResize(event, selectedHere, 'end')}
                      onPointerMove={handleResizeMove}
                      onPointerUp={handleResizeUp}
                    >
                      <span aria-hidden="true" className="block h-1 w-6 rounded-full bg-ink" />
                    </button>
                  )}
                  {busy && (
                    <span
                      aria-hidden="true"
                      className="font-display text-[11px] font-bold tabular-nums text-busy-ink"
                    >
                      &bull;
                    </span>
                  )}
                  {occurrence && <span className="sr-only">{t('a11y.busy')}</span>}
                  {/* Non-identifying by contract (D-076): a neutral disc, and a
                      "+N" cap when the segment covers more than one friend —
                      never a per-friend hue or avatar. */}
                  {glyphFriends.length > 0 && (
                    <span
                      aria-hidden="true"
                      className="inline-flex h-[18px] min-w-[18px] items-center justify-center rounded-full border border-ink/40 bg-busy px-1 text-[10px] font-bold leading-none text-busy-ink"
                      data-layer="friend"
                    >
                      {glyphFriends.length > 1 ? `+${glyphFriends.length}` : ''}
                    </span>
                  )}
                  {sessionLabelsAt(day, slot).map((session) => (
                    <span
                      key={session.id}
                      aria-hidden="true"
                      className={`block truncate rounded-xl border-2 border-ink/70 bg-accent px-1.5 py-0.5 font-display text-[11px] leading-tight text-accent-ink ${
                        session.viewerRelation === 'inherited'
                          ? 'italic font-semibold'
                          : 'font-extrabold'
                      }`}
                      data-layer="session"
                      data-viewer-relation={session.viewerRelation}
                    >
                      {session.viewerRelation === 'inherited'
                        ? `${session.title} (${t('a11y.inherited')})`
                        : session.title}
                    </span>
                  ))}
                  {cellPollOptions.map((option) => (
                    <span
                      key={option.id}
                      aria-hidden="true"
                      // Same pill geometry as a session span, on the ratified card/ink pair with
                      // a dashed edge for the "proposed, not booked" read — never `free-ink` on a
                      // new fill, which would be an unratified pairing the axe lane has to
                      // exclude (CALENDAR_DESIGN.md §9 S2).
                      className="block truncate rounded-xl border-2 border-dashed border-ink/45 bg-card px-1.5 py-0.5 font-display text-[11px] font-bold leading-tight text-ink"
                      data-layer="poll-option"
                    >
                      {option.pollTitle}
                    </span>
                  ))}
                  {/* Presentational only (§9 S2): the accessible name already
                      carries the slot's own time, so a screen reader gains
                      nothing from a second "it is now" node. */}
                  {nowMarker && nowMarker.day === day && nowMarker.slot === slot && (
                    <span
                      aria-hidden="true"
                      className="pointer-events-none absolute inset-x-0 z-20 flex items-center"
                      data-layer="now"
                      style={{ top: `${nowMarker.fraction * 100}%` }}
                    >
                      <span className="block h-1.5 w-1.5 shrink-0 rounded-full bg-danger" />
                      <span className="block h-px flex-1 bg-danger" />
                    </span>
                  )}
                </div>
              )
            })}
          </div>
        ))}
      </div>
      <div aria-live="polite" className="sr-only" role="status">
        {liveMessage}
      </div>
    </div>
  )
}
