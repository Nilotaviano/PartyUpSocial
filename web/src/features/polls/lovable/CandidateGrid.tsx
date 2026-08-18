import { useCallback, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { cn } from '../../availability/lovable/utils'
import { GridLines, ResizeHandle } from '../../availability/lovable/GridLayers'
import { createGridGeometry, type GridBlock, type GridDrag } from '../../availability/lovable/gridEditing'
import { useGridGestures, type GridBlockPressProps } from '../../availability/lovable/useGridGestures'
import {
  ROW_H,
  SLOT_MIN,
  clamp,
  fmt,
  fmtRange,
  type Friend,
  type GameSession,
  type WeekLabels,
} from '../../availability/lovable/partyup-data'
import { candidateKey, type Candidate } from './pollData'

/**
 * The poll candidate picker, built on the shared grid core
 * (`../../availability/lovable/gridEditing.ts`, `useGridGestures.ts`,
 * `GridLayers.tsx`) that also drives the calendar's `WeekGrid`: the same
 * `ROW_H`/`SLOT_MIN` pixel geometry, the same gridline background, and — since
 * D-211 — the same press-and-drag paint gesture, long-press arm on touch,
 * non-passive `touchmove` veto and window auto-scroll. The gesture mechanics
 * are the core's, not a poll-local copy: this file owns only what a poll
 * candidate *is*, never how a finger or a mouse edits one.
 *
 * Dragging over empty space paints one candidate owning its own start *and*
 * end; there is no poll-wide duration for a band to reflow against.
 *
 * What differs from `WeekGrid`, deliberately:
 *  - The ARIA cell layer is the pointer target too (`pointer-events-auto`),
 *    not just an AT surface, so the discrete tap survives alongside the drag:
 *    a press that never moved places one option at the last length used on
 *    this grid (60 minutes until one is), which is also what Enter on a cell
 *    does. A cell covered by a band turns pointer-transparent so the band
 *    beneath it receives the press instead.
 *  - Only a bottom resize handle exists. A band is anchored at its own start,
 *    which leaves a top handle with nothing to move under the finger; the
 *    handle changes that one option's length, no other.
 *  - No other-poll-options layer: dashed pills would collide with the
 *    candidate bands this grid exists to place.
 *
 * `Candidate`/`candidateKey` live in `./pollData` so this file exports only
 * the component (`react-refresh/only-export-components`); the `Candidate` type
 * is re-exported for callers that only need this module's props.
 */

export type { Candidate }

/** Duration is edited in the same 15-minute increments D-131 validates. */
const DURATION_STEP = SLOT_MIN

/** What a tapped option lasts before this grid has seen any other length. */
const DEFAULT_DURATION_MIN = 60

// Only a `free` window counts toward "N friends free": a `maybe` window is a
// weaker signal (the shipped calendar paints the two differently, D-135) and
// this grid has no separate maybe treatment of its own, so a maybe window is
// excluded from the count rather than counted as full availability.
function freeFriends(friends: Friend[], day: number, start: number, durationMin: number) {
  return friends.filter((f) =>
    f.windows.some(
      (w) =>
        w.intent === 'free' &&
        w.day === day &&
        w.start <= start &&
        w.end >= start + durationMin,
    ),
  )
}

type Props = {
  days: number[]
  rangeStart: number
  rangeEnd: number
  /** Placement granularity, and the granularity of the `candidate-slot-*` cells. */
  step?: number
  candidates: Candidate[]
  /** A painted band or a tapped cell: one new option owning its own start and length. */
  onCreate: (c: Candidate) => void
  onRemove: (c: Candidate) => void
  onMove: (from: Candidate, to: Candidate) => void
  /** D-211: the handle (or Shift+Arrow) edits this one option's length. */
  onResize: (c: Candidate, durationMinutes: number) => void
  max: number
  /**
   * Whether the cap is reached, computed by the caller across *all*
   * candidates (not just the ones falling in the currently displayed week).
   * Defaults to `candidates.length >= max` for callers with nothing to
   * filter (a single-week view where `candidates` is already the full set).
   */
  capReached?: boolean
  todayIndex?: number
  friends: Friend[]
  sessions: GameSession[]
  labels: WeekLabels
  /** Keyboard navigation past the shown day window pages it, as the calendar does. */
  onPageStep?: (delta: -1 | 1) => void
}

export function CandidateGrid({
  days,
  rangeStart,
  rangeEnd,
  step = 30,
  candidates,
  onCreate,
  onRemove,
  onMove,
  onResize,
  max,
  capReached,
  todayIndex,
  friends,
  sessions: allSessions,
  labels,
  onPageStep,
}: Props) {
  const { t } = useTranslation('polls')
  const [liveMessage, setLiveMessage] = useState('')
  /**
   * The length a tap places, and the one a painted band or a resize hands
   * forward: the grid remembers the last length the organizer expressed here
   * so a run of taps does not each need a resize afterwards.
   */
  const [placementDuration, setPlacementDuration] = useState(DEFAULT_DURATION_MIN)
  const rootRef = useRef<HTMLDivElement>(null)
  const columnsRef = useRef<HTMLDivElement>(null)
  /**
   * A pointer paint ends in a native `click` on the cell it started from, and
   * that click is the discrete tap path — so a committed paint has to swallow
   * exactly that one click, on exactly that one cell. Any later pointerdown
   * clears it, so a drag that ended elsewhere cannot swallow a real tap.
   */
  const suppressClickOn = useRef<EventTarget | null>(null)
  /** The cell the live paint began on, so its own click can be swallowed. */
  const paintOrigin = useRef<EventTarget | null>(null)

  const totalMin = rangeEnd - rangeStart
  const full = capReached ?? candidates.length >= max

  const geometry = useMemo(
    () => createGridGeometry({ rangeStart, rangeEnd, slotMin: SLOT_MIN, rowH: ROW_H, rowStepMin: step }),
    [rangeStart, rangeEnd, step],
  )
  const { pixelHeight: height, rowCount, top, span: hgt, minutesOfRow } = geometry

  const [focusCell, setFocusCell] = useState({ col: 0, row: 0 })
  const focusCol = clamp(focusCell.col, 0, Math.max(days.length - 1, 0))
  const focusRow = clamp(focusCell.row, 0, Math.max(rowCount - 1, 0))

  const announce = useCallback(
    (key: string, day: number, start: number, durationMinutes: number) =>
      setLiveMessage(
        t(key, {
          day: labels.dayNames[day] ?? '',
          range: fmtRange(start, start + durationMinutes),
        }),
      ),
    [t, labels],
  )

  const byKey = useMemo(() => {
    const map = new Map<string, Candidate>()
    for (const c of candidates) map.set(candidateKey(c), c)
    return map
  }, [candidates])

  const gestures = useGridGestures({
    columnsRef,
    hostRef: rootRef,
    rangeStart,
    rangeEnd,
    createStepMin: step,
    resizeStepMin: DURATION_STEP,
    minDurationMin: DURATION_STEP,
    initialCreateMin: 0,
    initialTouchCreateMin: 0,
    moveEnabled: true,
    resizeEnabled: true,
    capReached: full,
    onBlockCreate: (day, startMin, endMin, at) => {
      void at
      suppressClickOn.current = paintOrigin.current
      const durationMinutes = endMin - startMin
      onCreate({ day, start: startMin, durationMinutes })
      setPlacementDuration(durationMinutes)
      announce('a11y.candidateAdded', day, startMin, durationMinutes)
    },
    onBlockMove: (blockId, day, startMin, endMin) => {
      const from = byKey.get(blockId)
      if (!from) return
      onMove(from, { day, start: startMin, durationMinutes: endMin - startMin })
      announce('a11y.candidateMoved', day, startMin, endMin - startMin)
    },
    onBlockResize: (blockId, startMin, endMin) => {
      const c = byKey.get(blockId)
      if (!c) return
      const durationMinutes = endMin - startMin
      onResize(c, durationMinutes)
      setPlacementDuration(durationMinutes)
      setLiveMessage(t('a11y.durationChanged', { minutes: durationMinutes }))
    },
    onBandTap: (blockId) => {
      const c = byKey.get(blockId)
      if (!c) return
      onRemove(c)
      announce('a11y.candidateRemoved', c.day, c.start, c.durationMinutes)
    },
  })

  const byDay = useMemo(() => {
    const map = new Map<number, Candidate[]>()
    for (const c of candidates) {
      const arr = map.get(c.day) ?? []
      arr.push(c)
      map.set(c.day, arr)
    }
    for (const arr of map.values()) arr.sort((a, b) => a.start - b.start)
    return map
  }, [candidates])

  /** The candidate covering `m` on `day`, if any — the band drawn over that cell. */
  function coveringCandidate(day: number, m: number) {
    return (
      (byDay.get(day) ?? []).find((c) => c.start <= m && c.start + c.durationMinutes > m) ?? null
    )
  }

  function placeAt(day: number, m: number) {
    const covering = coveringCandidate(day, m)
    if (covering) {
      onRemove(covering)
      announce('a11y.candidateRemoved', covering.day, covering.start, covering.durationMinutes)
      return
    }
    if (full) {
      setLiveMessage(t('a11y.candidateCapReached', { max }))
      return
    }
    const durationMinutes = clamp(placementDuration, DURATION_STEP, Math.max(totalMin, DURATION_STEP))
    onCreate({ day, start: m, durationMinutes })
    announce('a11y.candidateAdded', day, m, durationMinutes)
  }

  function moveFocus(col: number, row: number) {
    if (col < 0) {
      onPageStep?.(-1)
      return
    }
    if (col > days.length - 1) {
      onPageStep?.(1)
      return
    }
    const next = { col, row: clamp(row, 0, rowCount - 1) }
    setFocusCell(next)
    const cell = rootRef.current?.querySelector<HTMLElement>(`[data-cell="${next.col}:${next.row}"]`)
    cell?.focus()
  }

  function onCellKeyDown(event: React.KeyboardEvent<HTMLDivElement>) {
    switch (event.key) {
      case 'ArrowUp':
        event.preventDefault()
        moveFocus(focusCol, focusRow - 1)
        return
      case 'ArrowDown':
        event.preventDefault()
        moveFocus(focusCol, focusRow + 1)
        return
      case 'ArrowLeft':
        event.preventDefault()
        moveFocus(focusCol - 1, focusRow)
        return
      case 'ArrowRight':
        event.preventDefault()
        moveFocus(focusCol + 1, focusRow)
        return
      case 'Home':
        event.preventDefault()
        moveFocus(focusCol, 0)
        return
      case 'End':
        event.preventDefault()
        moveFocus(focusCol, rowCount - 1)
        return
      case 'Enter':
      case ' ': {
        event.preventDefault()
        const day = days[focusCol]
        if (day === undefined) return
        placeAt(day, minutesOfRow(focusRow))
        return
      }
      default:
    }
  }

  function nudgeBand(c: Candidate, deltaDays: number, deltaMinutes: number) {
    const columnIndex = days.indexOf(c.day)
    const nextColumn = clamp(columnIndex + deltaDays, 0, days.length - 1)
    const day = days[nextColumn] ?? c.day
    const start = clamp(c.start + deltaMinutes, rangeStart, rangeEnd - step)
    if (day === c.day && start === c.start) return
    onMove(c, { day, start, durationMinutes: c.durationMinutes })
    announce('a11y.candidateMoved', day, start, c.durationMinutes)
  }

  function nudgeDuration(c: Candidate, delta: number) {
    const next = clamp(c.durationMinutes + delta, DURATION_STEP, totalMin)
    if (next === c.durationMinutes) return
    onResize(c, next)
    setPlacementDuration(next)
    setLiveMessage(t('a11y.durationChanged', { minutes: next }))
  }

  const hours: number[] = []
  for (let m = rangeStart; m <= rangeEnd; m += 60) hours.push(m)

  const gridTemplate = `56px repeat(${days.length}, minmax(0,1fr))`

  return (
    <div
      ref={rootRef}
      className="relative min-w-0"
      data-candidate-grid-root
      data-touch-dragging={gestures.active}
    >
      {/* header row */}
      <div className="sticky top-0 z-30 -mx-1 bg-card px-1 pt-2 pb-2">
        <div className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }}>
          <div />
          {days.map((d) => {
            const isToday = d === todayIndex
            return (
              <div
                key={d}
                className={cn(
                  'flex min-w-0 flex-col items-center rounded-xl border px-1 py-1.5 text-center',
                  isToday ? 'border-primary bg-primary/15' : 'border-border bg-card',
                )}
              >
                <span className="font-display text-[13px] leading-tight font-bold text-ink">
                  {labels.dayNames[d]}
                </span>
                <span className="truncate text-[11px] text-ink-soft">{labels.dayDates[d]}</span>
              </div>
            )
          })}
        </div>
      </div>

      {/* grid body */}
      <div className="relative mt-1">
        <div className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }} ref={columnsRef}>
          {/* hour ruler — full-opacity ink on every label (the prototype's /60
              half-hour labels sat at 2.54:1, under WCAG 1.4.3's 4.5:1, D-139). */}
          <div className="relative" style={{ height }}>
            {hours.map((m) => (
              <div
                key={m}
                className="absolute right-2 -translate-y-1/2 font-display text-[11px] font-semibold text-ink-soft tabular-nums"
                style={{ top: top(m) }}
              >
                {fmt(m)}
              </div>
            ))}
          </div>

          {days.map((day) => {
            const dayCandidates = byDay.get(day) ?? []
            const sessions = allSessions.filter((s) => s.day === day)
            const friendWindows = friends
              .flatMap((f) => f.windows.filter((w) => w.day === day).map((w) => ({ ...w, friend: f })))
              .sort((a, b) => a.start - b.start)

            // group friend chips into 30-min buckets at window starts, exactly
            // as the calendar paints them
            const buckets = new Map<number, typeof friendWindows>()
            for (const w of friendWindows) {
              const key = Math.floor(w.start / 30) * 30
              const arr = buckets.get(key) ?? []
              arr.push(w)
              buckets.set(key, arr)
            }

            const isToday = day === todayIndex
            const livePreview = gestures.previewFor(day)
            const paint =
              livePreview && livePreview.mode === 'create' && livePreview.painted ? livePreview : null

            return (
              <div
                key={day}
                data-daycol={day}
                className={cn(
                  'relative overflow-hidden rounded-2xl border bg-card select-none',
                  isToday ? 'border-primary' : 'border-border',
                  gestures.active ? 'touch-none' : 'touch-pan-y',
                )}
                style={{ height }}
              >
                <GridLines geometry={geometry} />

                {/* friends' availability chips */}
                {[...buckets.entries()].map(([startKey, group]) => {
                  const shown = group.slice(0, 3)
                  const rest = group.length - shown.length
                  return (
                    <div
                      key={startKey}
                      className="pointer-events-none absolute left-1.5 z-10 flex items-center gap-0.5"
                      style={{ top: top(group[0]!.start) + 2 }}
                    >
                      {shown.map((w) => (
                        <span
                          key={w.friend.id}
                          title={`${w.friend.name} · ${fmtRange(w.start, w.end)}`}
                          className="grid place-items-center rounded-full"
                        >
                          <AvatarDisc
                            seed={w.friend.id}
                            emoji={w.friend.emoji}
                            imgUrl={w.friend.imgUrl}
                            masked={w.friend.masked}
                            size="xxs"
                          />
                          {/* The disc is aria-hidden decoration and `title` is
                              mouse-only, so a screen reader viewer otherwise
                              gets nothing for this chip. */}
                          <span className="sr-only">
                            {t('a11y.friendWindow', {
                              name: w.friend.name,
                              range: fmtRange(w.start, w.end),
                            })}
                          </span>
                        </span>
                      ))}
                      {rest > 0 && (
                        <span className="grid h-[19px] min-w-[19px] place-items-center rounded-full border border-border bg-muted px-1 text-[9px] font-bold text-ink">
                          +{rest}
                        </span>
                      )}
                    </div>
                  )
                })}

                {/* scheduled sessions */}
                {sessions.map((s) => (
                  <div
                    key={s.id}
                    className="pointer-events-none absolute right-1.5 left-[38%] z-10 overflow-hidden rounded-xl border-2 border-ink bg-accent px-1.5 py-1 text-accent-foreground"
                    style={{ top: top(s.start) + 2, minHeight: 26, height: hgt(s.start, s.end) - 4 }}
                    data-testid={`candidate-session-${s.id}`}
                  >
                    <p className="truncate font-display text-[11px] leading-tight font-extrabold">
                      🎮 {s.title}
                    </p>
                    <p className="truncate text-[10px] leading-tight font-semibold">
                      {fmt(s.start)} · {t('create.grid.sessionCount', { occupancy: s.occupancy })}
                    </p>
                  </div>
                ))}

                {/* candidate bands */}
                {dayCandidates.map((c, index) => {
                  const key = candidateKey(c)
                  const block: GridBlock = {
                    id: key,
                    day: c.day,
                    startMin: c.start,
                    endMin: c.start + c.durationMinutes,
                  }
                  const live: GridDrag | null =
                    livePreview && livePreview.mode !== 'create' && livePreview.blockId === key
                      ? livePreview
                      : null
                  const liveStart = live ? live.a : c.start
                  const liveDuration = live ? live.b - live.a : c.durationMinutes
                  const depth = dayCandidates
                    .slice(0, index)
                    .filter((other) => other.start + other.durationMinutes > c.start).length
                  return (
                    <CandidateBand
                      key={key}
                      candidate={c}
                      start={liveStart}
                      durationMin={liveDuration}
                      depth={depth}
                      top={top(liveStart)}
                      height={Math.max(
                        hgt(liveStart, Math.min(liveStart + liveDuration, rangeEnd)),
                        18,
                      )}
                      onRemove={() => {
                        onRemove(c)
                        announce('a11y.candidateRemoved', c.day, c.start, c.durationMinutes)
                      }}
                      onNudge={(deltaDays, deltaMinutes) => nudgeBand(c, deltaDays, deltaMinutes)}
                      onDurationNudge={(delta) => nudgeDuration(c, delta)}
                      pressProps={gestures.blockPressProps(block)}
                      onBodyPointerDown={() => {
                        suppressClickOn.current = null
                      }}
                      resizeHandleProps={gestures.resizeHandleProps(block, 'end')}
                      onHandlePointerDown={() => {
                        suppressClickOn.current = null
                      }}
                      resizeLabel={t('create.grid.resizeDuration')}
                      bandLabel={t('a11y.candidateBand', {
                        day: labels.dayNames[c.day] ?? '',
                        range: fmtRange(c.start, c.start + c.durationMinutes),
                      })}
                    />
                  )
                })}

                {/* live paint preview */}
                {paint && (
                  <div
                    className="pointer-events-none absolute inset-x-1 z-30 rounded-xl border-2 border-primary bg-primary/25 px-2 py-1"
                    data-testid={`candidate-paint-${day}`}
                    style={{ top: top(paint.a), height: Math.max(hgt(paint.a, paint.b), 16) }}
                  >
                    <span className="font-display text-[11px] font-bold tabular-nums">
                      {fmtRange(paint.a, paint.b)}
                    </span>
                  </div>
                )}
              </div>
            )
          })}
        </div>

        {/* Keyboard/AT layer: the painted columns above carry the bands, chips
            and session pills; the same 30-minute slots are exposed here as a
            real ARIA grid that is also the pointer target — a press paints a
            band, a plain tap places one at the last length used. A cell a band
            already covers turns pointer-transparent so the band beneath it
            receives the press instead. */}
        <div
          aria-colcount={days.length + 1}
          aria-label={t('a11y.candidateGridLabel')}
          aria-rowcount={rowCount}
          className={cn(
            'pointer-events-none absolute inset-0 grid',
            gestures.active && 'touch-none',
          )}
          role="grid"
          style={{ gridTemplateRows: `repeat(${rowCount}, ${(step / SLOT_MIN) * ROW_H}px)` }}
          onKeyDown={onCellKeyDown}
        >
          {Array.from({ length: rowCount }, (_, row) => (
            <div
              key={row}
              aria-rowindex={row + 1}
              className="grid gap-1.5"
              role="row"
              style={{ gridTemplateColumns: gridTemplate }}
            >
              {/* The hiding lives on an inner span, never on the grid item
                  itself: `sr-only` is `position: absolute`, which would take
                  the row header out of the row's column flow and shift every
                  day cell one track left, off the column it names. */}
              <div aria-colindex={1} role="rowheader">
                <span className="sr-only">{fmt(minutesOfRow(row))}</span>
              </div>
              {days.map((day, col) => {
                const m = minutesOfRow(row)
                const covering = coveringCandidate(day, m)
                const isFocus = col === focusCol && row === focusRow
                const free = freeFriends(friends, day, m, placementDuration)
                const disabled = !covering && full
                // `columnProps` also carries `data-daycol`, which belongs on the
                // painted day column (above), never duplicated onto every one
                // of its per-row ARIA cells.
                const cellGestures = gestures.columnProps(day)
                const columnGestureProps = {
                  onPointerUp: cellGestures.onPointerUp,
                  onPointerCancel: cellGestures.onPointerCancel,
                  onPointerMove: cellGestures.onPointerMove,
                  onContextMenu: cellGestures.onContextMenu,
                }
                return (
                  <div
                    key={day}
                    aria-colindex={col + 2}
                    aria-disabled={disabled || undefined}
                    aria-label={
                      covering
                        ? t('a11y.candidateBand', {
                            day: labels.dayNames[day] ?? '',
                            range: fmtRange(
                              covering.start,
                              covering.start + covering.durationMinutes,
                            ),
                          })
                        : t('a11y.candidateFreeCount', {
                            day: labels.dayNames[day] ?? '',
                            time: fmt(m),
                            count: free.length,
                          })
                    }
                    aria-selected={covering ? true : undefined}
                    className={cn(
                      'rounded-sm outline-focus focus-visible:outline-2 focus-visible:outline-offset-1',
                      covering ? 'pointer-events-none' : 'pointer-events-auto',
                      !covering && !disabled && 'cursor-pointer hover:bg-primary/15',
                      disabled && 'cursor-not-allowed',
                    )}
                    data-cell={`${col}:${row}`}
                    data-disabled={disabled ? 'true' : undefined}
                    data-testid={`candidate-slot-${day}-${m}`}
                    role="gridcell"
                    tabIndex={isFocus ? 0 : -1}
                    {...columnGestureProps}
                    onClick={(e) => {
                      if (suppressClickOn.current === e.currentTarget) {
                        suppressClickOn.current = null
                        return
                      }
                      placeAt(day, m)
                    }}
                    onFocus={() => setFocusCell({ col, row })}
                    onPointerDown={(e) => {
                      suppressClickOn.current = null
                      paintOrigin.current = e.currentTarget
                      cellGestures.onPointerDown(e)
                    }}
                  />
                )
              })}
            </div>
          ))}
        </div>
      </div>

      <div aria-live="polite" className="sr-only" role="status">
        {liveMessage}
      </div>
    </div>
  )
}

function CandidateBand({
  candidate,
  start,
  durationMin,
  depth,
  top,
  height,
  bandLabel,
  resizeLabel,
  onRemove,
  onNudge,
  onDurationNudge,
  pressProps,
  onBodyPointerDown,
  resizeHandleProps,
  onHandlePointerDown,
}: {
  candidate: Candidate
  start: number
  durationMin: number
  depth: number
  top: number
  height: number
  bandLabel: string
  resizeLabel: string
  onRemove: () => void
  onNudge: (deltaDays: number, deltaMinutes: number) => void
  onDurationNudge: (delta: number) => void
  pressProps: GridBlockPressProps
  onBodyPointerDown: () => void
  resizeHandleProps: { onPointerDown: (e: React.PointerEvent<HTMLElement>) => void }
  onHandlePointerDown: () => void
}) {
  return (
    <div
      aria-label={bandLabel}
      className="absolute z-20 rounded-xl border-2 border-primary bg-primary px-2 py-1 text-left text-primary-foreground"
      data-testid={`candidate-band-${candidate.day}-${candidate.start}`}
      role="button"
      style={{ top, height, left: 6 + depth * 12, right: 6 }}
      tabIndex={0}
      onContextMenu={(e) => e.preventDefault()}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ' || e.key === 'Delete' || e.key === 'Backspace') {
          e.preventDefault()
          onRemove()
          return
        }
        if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
          e.preventDefault()
          const sign = e.key === 'ArrowUp' ? -1 : 1
          // Shift retargets the arrows at this option's own length — the
          // keyboard equivalent of its bottom resize handle.
          if (e.shiftKey) onDurationNudge(sign * DURATION_STEP)
          else onNudge(0, sign * 30)
          return
        }
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
          e.preventDefault()
          onNudge(e.key === 'ArrowLeft' ? -1 : 1, 0)
        }
      }}
      {...pressProps}
      onPointerDown={(e) => {
        onBodyPointerDown()
        pressProps.onPointerDown(e)
      }}
    >
      <span className="font-display text-[11px] leading-tight font-bold tabular-nums">
        {fmtRange(start, start + durationMin)}
      </span>
      <ResizeHandle
        position="bottom"
        label={resizeLabel}
        stepMin={DURATION_STEP}
        onNudge={onDurationNudge}
        onPointerDown={(e) => {
          onHandlePointerDown()
          resizeHandleProps.onPointerDown(e)
        }}
      />
    </div>
  )
}
