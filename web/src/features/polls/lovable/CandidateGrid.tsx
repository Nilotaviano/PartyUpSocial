import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { GripHorizontal } from 'lucide-react'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { cn } from '../../availability/lovable/utils'
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
 * The poll candidate picker, built on the calendar `WeekGrid`'s rendering
 * model (`../../availability/lovable/WeekGrid.tsx`): one continuous column per
 * day painted at `ROW_H` px per `SLOT_MIN` minutes, with the same gridline
 * background, the same friend-chip bucketing, the same session pills, and the
 * same transparent ARIA-grid layer carrying focus and names on top.
 *
 * What differs from `WeekGrid`, deliberately:
 *  - The ARIA cell layer is the pointer target too (`pointer-events-auto`),
 *    not just an AT surface: placement here is a discrete 30-minute tap, never
 *    a painted drag, so no long-press arming is needed to keep a finger drag
 *    scrolling the page. A cell covered by a candidate band turns
 *    pointer-transparent so the band beneath it receives the tap instead.
 *  - Only a bottom resize handle exists. A poll's options differ solely by
 *    start time, so a handle changes the poll's one shared duration and every
 *    band reflows; each band is anchored at its own start, which leaves a top
 *    handle with nothing to move under the finger.
 *  - No other-poll-options layer: dashed pills would collide with the
 *    candidate bands this grid exists to place.
 *
 * `Candidate`/`candidateKey` live in `./pollData` so this file exports only
 * the component (`react-refresh/only-export-components`); the `Candidate` type
 * is re-exported for callers that only need this module's props.
 */

export type { Candidate }

const AUTO_SCROLL_EDGE_PX = 48
const AUTO_SCROLL_STEP_PX = 16
const AUTO_SCROLL_TICK_MS = 60

const LONG_PRESS_MS = 380

/**
 * A finger never holds perfectly still: only movement past this radius is a
 * real scroll reclaiming the gesture from a pending long press.
 */
const MOVE_CANCEL_PX = 10

/** Duration is edited in the same 15-minute increments D-131 validates. */
const DURATION_STEP = SLOT_MIN

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

function snapTo(mins: number, unit: number) {
  return Math.round(mins / unit) * unit
}

type Drag =
  | {
      mode: 'move'
      key: string
      day: number
      start: number
      originStart: number
    }
  | {
      mode: 'duration'
      day: number
      bandStart: number
      minutes: number
      originMinutes: number
    }
  | null

type Props = {
  days: number[]
  rangeStart: number
  rangeEnd: number
  /** Placement granularity, and the granularity of the `candidate-slot-*` cells. */
  step?: number
  durationMin: number
  candidates: Candidate[]
  onToggle: (c: Candidate) => void
  onMove: (from: Candidate, to: Candidate) => void
  onDurationChange: (minutes: number) => void
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
  durationMin,
  candidates,
  onToggle,
  onMove,
  onDurationChange,
  max,
  capReached,
  todayIndex,
  friends,
  sessions: allSessions,
  labels,
  onPageStep,
}: Props) {
  const { t } = useTranslation('polls')
  const [drag, setDrag] = useState<Drag>(null)
  const [armed, setArmed] = useState(false)
  const [liveMessage, setLiveMessage] = useState('')
  const columnsRef = useRef<HTMLDivElement>(null)
  const pressTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const touchOrigin = useRef<{ x: number; y: number } | null>(null)
  /**
   * The committed drag is read from here, never from `drag`: a pointermove's
   * render can still be pending when the pointerup lands, so every mutation
   * writes this ref in the same DOM event that queues the state update.
   */
  const dragRef = useRef<Drag>(null)
  /** Mirrors `armed` for the DOM-level `touchmove` veto, which cannot read state. */
  const armedRef = useRef(false)
  const applyDrag = useCallback((next: Drag) => {
    dragRef.current = next
    setDrag(next)
  }, [])

  const cancelPress = useCallback(() => {
    if (pressTimer.current) clearTimeout(pressTimer.current)
    pressTimer.current = null
    touchOrigin.current = null
  }, [])

  /**
   * A gesture the browser takes back must leave nothing behind: no pending
   * press, no armed state, and no in-flight move or duration drag that a later
   * pointerup would commit.
   */
  const abortGesture = useCallback(() => {
    cancelPress()
    armedRef.current = false
    setArmed(false)
    applyDrag(null)
  }, [applyDrag, cancelPress])

  /**
   * iOS locks `touch-action` when a gesture starts, so flipping the column's
   * class mid-gesture cannot stop the pan it already granted. Vetoing
   * `touchmove` from a non-passive listener is the only scroll refusal it
   * honors, and only while a band's long press is pending or armed — a plain
   * finger drag, and the tap that places a candidate, are untouched.
   */
  useEffect(() => {
    const el = columnsRef.current
    if (!el) return
    const onTouchMove = (event: TouchEvent) => {
      if (pressTimer.current === null && !armedRef.current) return
      event.preventDefault()
    }
    el.addEventListener('touchmove', onTouchMove, { passive: false })
    return () => el.removeEventListener('touchmove', onTouchMove)
  }, [])

  const totalMin = rangeEnd - rangeStart
  const height = (totalMin / SLOT_MIN) * ROW_H
  const rowCount = Math.max(1, Math.round(totalMin / step))
  const full = capReached ?? candidates.length >= max

  const top = useCallback((m: number) => ((m - rangeStart) / SLOT_MIN) * ROW_H, [rangeStart])
  const hgt = useCallback((a: number, b: number) => ((b - a) / SLOT_MIN) * ROW_H, [])

  const minutesOfRow = useCallback((row: number) => rangeStart + row * step, [rangeStart, step])

  /**
   * Gridlines are real elements, not a `repeating-linear-gradient`: iOS Safari
   * rounds each gradient tile independently at fractional device pixel ratios,
   * so the painted spacing drifts away from the hour labels and bands down the
   * column. The same `top()` the labels use keeps them aligned by construction.
   */
  const gridLines = useMemo(() => {
    const lines: { m: number; hour: boolean }[] = []
    for (let m = rangeStart; m < rangeEnd; m += SLOT_MIN) lines.push({ m, hour: m % 60 === 0 })
    return lines
  }, [rangeStart, rangeEnd])

  const [focusCell, setFocusCell] = useState({ col: 0, row: 0 })
  const focusCol = clamp(focusCell.col, 0, Math.max(days.length - 1, 0))
  const focusRow = clamp(focusCell.row, 0, Math.max(rowCount - 1, 0))

  const yToMin = useCallback(
    (clientY: number, el: HTMLElement, unit: number) => {
      const rect = el.getBoundingClientRect()
      const raw = rangeStart + ((clientY - rect.top) / (rect.height || 1)) * totalMin
      return clamp(snapTo(raw, unit), rangeStart, rangeEnd)
    },
    [rangeStart, rangeEnd, totalMin],
  )

  const onMoveRef = useRef(onMove)
  const onDurationChangeRef = useRef(onDurationChange)
  const onToggleRef = useRef(onToggle)
  useEffect(() => {
    onMoveRef.current = onMove
    onDurationChangeRef.current = onDurationChange
    onToggleRef.current = onToggle
  })

  const rangeLabel = useCallback(
    (start: number) => fmtRange(start, start + durationMin),
    [durationMin],
  )

  const announce = useCallback(
    (key: string, day: number, start: number) =>
      setLiveMessage(
        t(key, { day: labels.dayNames[day] ?? '', range: fmtRange(start, start + durationMin) }),
      ),
    [t, labels, durationMin],
  )

  // Global pointer handling while dragging. Keyed on whether a drag is live,
  // never on the drag itself: re-running on every pointermove would restart
  // the auto-scroll interval and throw away the pointer position it steers by.
  const dragging = drag !== null
  useEffect(() => {
    if (!dragging) return
    let pointerY: number | null = null
    const columnOf = () =>
      columnsRef.current?.querySelector<HTMLElement>(
        `[data-daycol="${dragRef.current ? dragRef.current.day : ''}"]`,
      )
    const extendTo = (m: number) => {
      const d = dragRef.current
      if (!d) return
      if (d.mode === 'move') {
        applyDrag({ ...d, start: m })
      } else {
        applyDrag({ ...d, minutes: Math.max(DURATION_STEP, m - d.bandStart) })
      }
    }
    const move = (e: PointerEvent) => {
      const col = columnOf()
      const d = dragRef.current
      if (!col || !d) return
      pointerY = e.clientY
      extendTo(yToMin(e.clientY, col, d.mode === 'move' ? step : DURATION_STEP))
      e.preventDefault()
    }
    const autoScroll = setInterval(() => {
      if (pointerY === null) return
      const distanceToBottom = window.innerHeight - pointerY
      const delta =
        pointerY < AUTO_SCROLL_EDGE_PX
          ? -AUTO_SCROLL_STEP_PX
          : distanceToBottom < AUTO_SCROLL_EDGE_PX
            ? AUTO_SCROLL_STEP_PX
            : 0
      if (delta === 0) return
      window.scrollBy(0, delta)
      const col = columnOf()
      const d = dragRef.current
      if (col && d) extendTo(yToMin(pointerY, col, d.mode === 'move' ? step : DURATION_STEP))
    }, AUTO_SCROLL_TICK_MS)
    const up = () => {
      const d = dragRef.current
      applyDrag(null)
      setArmed(false)
      armedRef.current = false
      if (!d) return
      if (d.mode === 'move') {
        // A press that never moved is a tap on the band: today's toggle
        // semantics, so it removes the candidate it landed on.
        if (d.start === d.originStart) {
          onToggleRef.current({ day: d.day, start: d.originStart })
          announce('a11y.candidateRemoved', d.day, d.originStart)
          return
        }
        onMoveRef.current({ day: d.day, start: d.originStart }, { day: d.day, start: d.start })
        announce('a11y.candidateMoved', d.day, d.start)
        return
      }
      if (d.minutes !== d.originMinutes) {
        onDurationChangeRef.current(d.minutes)
        setLiveMessage(t('a11y.durationChanged', { minutes: d.minutes }))
      }
    }
    window.addEventListener('pointermove', move, { passive: false })
    window.addEventListener('pointerup', up)
    window.addEventListener('pointercancel', abortGesture)
    return () => {
      clearInterval(autoScroll)
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      window.removeEventListener('pointercancel', abortGesture)
    }
  }, [abortGesture, announce, applyDrag, dragging, step, t, yToMin])

  useEffect(() => cancelPress, [cancelPress])

  /** The live duration while a resize drag is in flight; committed value otherwise. */
  const previewDuration =
    drag && drag.mode === 'duration' ? drag.minutes : durationMin

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
    return (byDay.get(day) ?? []).find((c) => c.start <= m && c.start + previewDuration > m) ?? null
  }

  function toggleAt(day: number, m: number) {
    const covering = coveringCandidate(day, m)
    if (covering) {
      onToggle(covering)
      announce('a11y.candidateRemoved', covering.day, covering.start)
      return
    }
    if (full) {
      setLiveMessage(t('a11y.candidateCapReached', { max }))
      return
    }
    onToggle({ day, start: m })
    announce('a11y.candidateAdded', day, m)
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
    const cell = columnsRef.current
      ?.closest('[data-candidate-grid-root]')
      ?.querySelector<HTMLElement>(`[data-cell="${next.col}:${next.row}"]`)
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
        toggleAt(day, minutesOfRow(focusRow))
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
    onMove(c, { day, start })
    announce('a11y.candidateMoved', day, start)
  }

  function changeDuration(delta: number) {
    const next = clamp(durationMin + delta, DURATION_STEP, totalMin)
    if (next === durationMin) return
    onDurationChange(next)
    setLiveMessage(t('a11y.durationChanged', { minutes: next }))
  }

  const hours: number[] = []
  for (let m = rangeStart; m <= rangeEnd; m += 60) hours.push(m)

  const gridTemplate = `56px repeat(${days.length}, minmax(0,1fr))`

  return (
    <div
      className="relative min-w-0"
      data-candidate-grid-root
      data-touch-dragging={armed || drag !== null}
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

            return (
              <div
                key={day}
                data-daycol={day}
                className={cn(
                  'relative overflow-hidden rounded-2xl border bg-card select-none',
                  isToday ? 'border-primary' : 'border-border',
                  armed || drag ? 'touch-none' : 'touch-pan-y',
                )}
                style={{ height }}
              >
                {/* gridlines */}
                <div aria-hidden="true" className="pointer-events-none absolute inset-0">
                  {gridLines.map((line) => (
                    <div
                      key={line.m}
                      className="absolute inset-x-0 h-px"
                      data-gridline={line.hour ? 'hour' : 'quarter'}
                      style={{
                        top: top(line.m),
                        background: line.hour ? 'var(--grid-hour)' : 'var(--grid-line)',
                      }}
                    />
                  ))}
                </div>

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
                  const live =
                    drag && drag.mode === 'move' && drag.key === candidateKey(c)
                      ? drag.start
                      : c.start
                  const depth = dayCandidates
                    .slice(0, index)
                    .filter((other) => other.start + previewDuration > c.start).length
                  return (
                    <CandidateBand
                      key={candidateKey(c)}
                      candidate={c}
                      start={live}
                      durationMin={previewDuration}
                      depth={depth}
                      top={top(live)}
                      height={Math.max(hgt(live, Math.min(live + previewDuration, rangeEnd)), 18)}
                      onRemove={() => {
                        onToggle(c)
                        announce('a11y.candidateRemoved', c.day, c.start)
                      }}
                      onNudge={(deltaDays, deltaMinutes) => nudgeBand(c, deltaDays, deltaMinutes)}
                      onDurationNudge={changeDuration}
                      onBodyPointerDown={(e) => {
                        if (e.button === 2) return
                        e.stopPropagation()
                        const begin = () =>
                          applyDrag({
                            mode: 'move',
                            key: candidateKey(c),
                            day: c.day,
                            start: c.start,
                            originStart: c.start,
                          })
                        if (e.pointerType === 'touch') {
                          // A plain finger drag must scroll the page: moving a
                          // band takes a long press, as the calendar requires
                          // for painting.
                          touchOrigin.current = { x: e.clientX, y: e.clientY }
                          pressTimer.current = setTimeout(() => {
                            pressTimer.current = null
                            touchOrigin.current = null
                            armedRef.current = true
                            setArmed(true)
                            if (navigator.vibrate) navigator.vibrate(12)
                            begin()
                          }, LONG_PRESS_MS)
                          return
                        }
                        begin()
                      }}
                      onBodyPointerMove={(e) => {
                        const origin = touchOrigin.current
                        if (!origin) return
                        if (Math.hypot(e.clientX - origin.x, e.clientY - origin.y) > MOVE_CANCEL_PX) {
                          cancelPress()
                        }
                      }}
                      onBodyPointerCancel={abortGesture}
                      onBodyPointerUp={() => {
                        // Touch tap: the long press never fired, so this is a
                        // plain tap and removes the band.
                        if (pressTimer.current) {
                          cancelPress()
                          onToggle(c)
                          announce('a11y.candidateRemoved', c.day, c.start)
                        }
                      }}
                      onHandlePointerDown={(e) => {
                        e.stopPropagation()
                        applyDrag({
                          mode: 'duration',
                          day: c.day,
                          bandStart: c.start,
                          minutes: durationMin,
                          originMinutes: durationMin,
                        })
                      }}
                      resizeLabel={t('create.grid.resizeDuration')}
                      bandLabel={t('a11y.candidateBand', {
                        day: labels.dayNames[c.day] ?? '',
                        range: rangeLabel(c.start),
                      })}
                    />
                  )
                })}
              </div>
            )
          })}
        </div>

        {/* Keyboard/AT layer: the painted columns above carry the bands, chips
            and session pills; the same 30-minute slots are exposed here as a
            real ARIA grid that is also the pointer target for placement. A
            cell a band already covers turns pointer-transparent so the band
            beneath it receives the tap instead. */}
        <div
          aria-colcount={days.length + 1}
          aria-label={t('a11y.candidateGridLabel')}
          aria-rowcount={rowCount}
          className="pointer-events-none absolute inset-0 grid"
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
                const free = freeFriends(friends, day, m, durationMin)
                const disabled = !covering && full
                return (
                  <div
                    key={day}
                    aria-colindex={col + 2}
                    aria-disabled={disabled || undefined}
                    aria-label={
                      covering
                        ? t('a11y.candidateBand', {
                            day: labels.dayNames[day] ?? '',
                            range: rangeLabel(covering.start),
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
                    onClick={() => toggleAt(day, m)}
                    onFocus={() => setFocusCell({ col, row })}
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
  onBodyPointerDown,
  onBodyPointerMove,
  onBodyPointerUp,
  onBodyPointerCancel,
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
  onBodyPointerDown: (e: React.PointerEvent) => void
  onBodyPointerMove: (e: React.PointerEvent) => void
  onBodyPointerUp: () => void
  onBodyPointerCancel: () => void
  onHandlePointerDown: (e: React.PointerEvent) => void
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
          // Shift retargets the arrows at the poll's one shared duration —
          // the pointer equivalent of the bottom resize handle.
          if (e.shiftKey) onDurationNudge(sign * DURATION_STEP)
          else onNudge(0, sign * 30)
          return
        }
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
          e.preventDefault()
          onNudge(e.key === 'ArrowLeft' ? -1 : 1, 0)
        }
      }}
      onPointerDown={onBodyPointerDown}
      onPointerMove={onBodyPointerMove}
      onPointerUp={onBodyPointerUp}
      onPointerCancel={onBodyPointerCancel}
    >
      <span className="font-display text-[11px] leading-tight font-bold tabular-nums">
        {fmtRange(start, start + durationMin)}
      </span>
      {/* Pointer-only affordance: the band's own Shift+Arrow keys are the
          keyboard path, so this stays out of the tab order entirely. */}
      <span
        aria-hidden="true"
        className="absolute -bottom-2 left-1/2 grid h-5 w-11 -translate-x-1/2 cursor-ns-resize touch-none place-items-center rounded-full border-2 border-primary bg-card"
        title={resizeLabel}
        onPointerDown={onHandlePointerDown}
      >
        <GripHorizontal className="size-3.5 text-ink" />
      </span>
    </div>
  )
}
