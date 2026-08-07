import { useEffect, useRef, useState } from 'react'
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
  onCreateBlock: (range: GridRange) => void
  onEditOccurrence: (occurrenceId: string) => void
  dayLabels: string[]
  timeLabelFormatter: (date: Date) => string
}

const SLOT_MINUTES = 15
const LONG_PRESS_MS = 400
const MOVE_CANCEL_PX = 10
const AUTO_SCROLL_EDGE_PX = 32
const AUTO_SCROLL_STEP_PX = 12

type Cursor = { day: number; slot: number }
type Selection = { day: number; anchorSlot: number; endSlot: number }

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

export function WeekGrid({
  weekStart,
  startHour = 0,
  endHour = 24,
  occurrences,
  merged,
  friendRanges = [],
  sessionEntries = [],
  onCreateBlock,
  onEditOccurrence,
  dayLabels,
  timeLabelFormatter,
}: WeekGridProps) {
  const { t } = useTranslation('availability')
  const slots = slotsPerDay(startHour, endHour)
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
  const dragging = useRef(false)
  const longPressTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const touchOrigin = useRef<{ x: number; y: number } | null>(null)
  const gridRef = useRef<HTMLDivElement>(null)
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
    const start = cellTime(weekStart, day, slot, startHour)
    const end = cellTime(weekStart, day, slot + 1, startHour)
    return occurrences.find((o) => overlaps(o.start, o.end, start, end))
  }

  function mergedAt(day: number, slot: number) {
    const start = cellTime(weekStart, day, slot, startHour)
    const end = cellTime(weekStart, day, slot + 1, startHour)
    return merged.some((range) => overlaps(range.start, range.end, start, end))
  }

  function friendsAt(day: number, slot: number) {
    const start = cellTime(weekStart, day, slot, startHour)
    const end = cellTime(weekStart, day, slot + 1, startHour)
    return friendRanges.filter((range) => overlaps(range.start, range.end, start, end))
  }

  function sessionsAt(day: number, slot: number) {
    const start = cellTime(weekStart, day, slot, startHour)
    const end = cellTime(weekStart, day, slot + 1, startHour)
    return sessionEntries.filter((session) => overlaps(session.start, session.end, start, end))
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
      t('a11y.selectionCommitted', {
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
      case 'Enter': {
        event.preventDefault()
        if (selection) {
          commitSelection(selection)
          break
        }
        const occurrence = occurrenceAt(day, slot)
        if (occurrence) {
          onEditOccurrence(occurrence.id)
        } else {
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

  function maybeAutoScroll(clientY: number) {
    const container = gridRef.current
    if (!container) return
    const rect = container.getBoundingClientRect()
    const distanceFromTop = clientY - rect.top
    const distanceFromBottom = rect.bottom - clientY

    stopAutoScroll()
    if (distanceFromTop >= 0 && distanceFromTop < AUTO_SCROLL_EDGE_PX) {
      const step = () => {
        container.scrollTop -= AUTO_SCROLL_STEP_PX
        autoScrollFrame.current = requestAnimationFrame(step)
      }
      autoScrollFrame.current = requestAnimationFrame(step)
    } else if (distanceFromBottom >= 0 && distanceFromBottom < AUTO_SCROLL_EDGE_PX) {
      const step = () => {
        container.scrollTop += AUTO_SCROLL_STEP_PX
        autoScrollFrame.current = requestAnimationFrame(step)
      }
      autoScrollFrame.current = requestAnimationFrame(step)
    }
  }

  function handleMouseDown(event: React.MouseEvent, day: number, slot: number) {
    if (event.button !== 0) return
    if (occurrenceAt(day, slot)) return
    dragging.current = true
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

  // Touch is a capability check (`PointerEvent` + `pointerType`), never UA
  // sniffing (design §10.3): a short tap does nothing, a long-press begins a
  // selection, and a press that turns into a scroll (movement past the
  // threshold before the timer fires) commits nothing (A-018/P5-TOUCH-01).
  function handlePointerDown(event: React.PointerEvent, day: number, slot: number) {
    if (event.pointerType !== 'touch') return
    if (occurrenceAt(day, slot)) return
    touchOrigin.current = { x: event.clientX, y: event.clientY }
    longPressTimer.current = setTimeout(() => {
      dragging.current = true
      setIsTouchDragging(true)
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
    if (!origin || !longPressTimer.current) return
    const movedPx = Math.hypot(event.clientX - origin.x, event.clientY - origin.y)
    if (movedPx > MOVE_CANCEL_PX) {
      clearTimeout(longPressTimer.current)
      longPressTimer.current = undefined
      touchOrigin.current = null
    }
  }

  function handlePointerUp(event: React.PointerEvent) {
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
      setSelection((current) => {
        if (current) commitSelection(current)
        return null
      })
    }
  }

  const slotIndexes = Array.from({ length: slots }, (_, index) => index)
  const dayIndexes = [0, 1, 2, 3, 4, 5, 6]

  return (
    <div>
      <div
        ref={gridRef}
        aria-colcount={8}
        aria-label={t('a11y.gridLabel')}
        aria-rowcount={slots + 1}
        className={`max-h-[70vh] overflow-y-auto ${isTouchDragging ? 'touch-none' : ''}`}
        data-touch-dragging={isTouchDragging}
        role="grid"
        onMouseUp={handleMouseUp}
      >
        <div className="flex" role="row">
          <div aria-colindex={1} className="min-h-11 w-16 shrink-0" role="columnheader" />
          {dayIndexes.map((day) => (
            <div
              key={day}
              aria-colindex={day + 2}
              className="min-h-11 min-w-11 flex-1 text-center text-sm font-medium"
              role="columnheader"
            >
              {dayLabels[day]}
            </div>
          ))}
        </div>
        {slotIndexes.map((slot) => (
          <div key={slot} className="flex" role="row">
            <div
              aria-colindex={1}
              aria-rowindex={slot + 2}
              className="min-h-11 w-16 shrink-0 pr-2 text-right text-xs text-slate-500"
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
              const cellSessions = sessionsAt(day, slot)
              const inSelection =
                selection &&
                selection.day === day &&
                slot >= Math.min(selection.anchorSlot, selection.endSlot) &&
                slot <= Math.max(selection.anchorSlot, selection.endSlot)
              const isFocused = focused.day === day && focused.slot === slot
              const key = `${day}:${slot}`
              const state = inSelection ? 'selected' : busy ? 'busy' : 'free'

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
                  className="min-h-11 min-w-11 flex-1 border border-slate-200 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600"
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
                  onKeyDown={(event) => handleKeyDown(event, day, slot)}
                  onMouseDown={(event) => handleMouseDown(event, day, slot)}
                  onMouseEnter={() => handleMouseEnter(day, slot)}
                  onPointerDown={(event) => handlePointerDown(event, day, slot)}
                  onPointerMove={(event) => handlePointerMove(event, day, slot)}
                  onPointerUp={handlePointerUp}
                >
                  {busy && (
                    <span aria-hidden="true" className="text-xs">
                      &bull;
                    </span>
                  )}
                  {occurrence && <span className="sr-only">{t('a11y.busy')}</span>}
                  {cellFriends.length > 0 && (
                    <span
                      aria-hidden="true"
                      className="block text-xs text-indigo-700"
                      data-layer="friend"
                    >
                      &#9675;
                    </span>
                  )}
                  {cellSessions.map((session) => (
                    <span
                      key={session.id}
                      aria-hidden="true"
                      className={`block truncate text-[10px] leading-tight ${
                        session.viewerRelation === 'inherited'
                          ? 'italic text-slate-600'
                          : 'font-semibold text-emerald-800'
                      }`}
                      data-layer="session"
                      data-viewer-relation={session.viewerRelation}
                    >
                      {session.viewerRelation === 'inherited'
                        ? `${session.title} (${t('a11y.inherited')})`
                        : session.title}
                    </span>
                  ))}
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
