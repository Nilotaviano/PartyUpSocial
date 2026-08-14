import { Check } from 'lucide-react'
import { cn } from '../../availability/lovable/utils'
import { AvatarDisc } from '../../friends/AvatarDisc'
import { fmt, type Friend, type WeekLabels } from '../../availability/lovable/partyup-data'
import { candidateKey, type Candidate } from './pollData'

/**
 * Ported from the Lovable prototype's `src/components/partyup/CandidateGrid.tsx`,
 * byte-faithful apart from:
 *  - `@/lib/utils` -> the already-ported `cn`.
 *  - `DAY_NAMES`/`shortDate` (mock module constants) -> a `labels: WeekLabels`
 *    prop, built by the caller from `Intl.DateTimeFormat` with
 *    `timeZone: 'UTC'` over fake-local dates (D-046), the same pattern
 *    `CalendarShell` uses for its own week labels.
 *  - `fmt` (minute-of-day formatting) is reused as-is from
 *    `../../availability/lovable/partyup-data` rather than duplicated.
 *  - `FRIENDS` (mock module constant) -> a `friends: Friend[]` prop, and each
 *    chip renders through the shared `AvatarDisc` (seed/emoji/imgUrl/masked)
 *    instead of the prototype's own `var(--chip-N)` swatch.
 *  - The `aria-label` string moves to a caller-supplied `freeCountLabel`
 *    function (a tiny presentational bit takes a label prop rather than its
 *    own `useTranslation` hook).
 *  - `Candidate`/`candidateKey` live in `./pollData`, not here: this file
 *    exports only the component, so `react-refresh/only-export-components`
 *    stays green. Only the `Candidate` type is re-exported below, for
 *    callers that only need this module's props — `candidateKey` is a
 *    function, so re-exporting it here would trip the same lint rule this
 *    split exists to satisfy; callers needing it import it from `./pollData`
 *    directly.
 *
 * This component owns only the `max`-candidate cap (disabling further taps
 * once `candidates.length >= max`, still allowing an already-selected slot to
 * be un-toggled) — duplicate-tap merge and any 12-candidate validation
 * message are `onToggle`'s caller's concern (a page, out of S1's scope).
 */

export type { Candidate }

// Only a `free` window counts toward "N friends free": a `maybe` window is a
// weaker signal (the shipped calendar paints the two differently, D-135) and
// this prototype grid has no separate maybe treatment of its own, so a maybe
// window is simply excluded from the count and heat rather than counted as
// full availability.
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
  step?: number
  durationMin: number
  candidates: Candidate[]
  onToggle: (c: Candidate) => void
  max: number
  todayIndex?: number
  friends: Friend[]
  labels: WeekLabels
  freeCountLabel: (dayLabel: string, timeLabel: string, count: number) => string
}

export function CandidateGrid({
  days,
  rangeStart,
  rangeEnd,
  step = 30,
  durationMin,
  candidates,
  onToggle,
  max,
  todayIndex,
  friends,
  labels,
  freeCountLabel,
}: Props) {
  const slots: number[] = []
  for (let m = rangeStart; m + durationMin <= rangeEnd; m += step) slots.push(m)
  const selected = new Set(candidates.map(candidateKey))

  return (
    <div className="min-w-0">
      <div
        className="grid gap-1"
        style={{ gridTemplateColumns: `48px repeat(${days.length}, minmax(0,1fr))` }}
      >
        <div className="sticky top-0 z-20 bg-card pb-1" />
        {days.map((d) => (
          <div key={d} className="sticky top-0 z-20 bg-card pb-1 text-center">
            <p className="font-display text-[13px] font-extrabold">
              {labels.dayNames[d]}
              {d === todayIndex && <span className="ml-1 text-primary">•</span>}
            </p>
            <p className="text-[11px] text-muted-foreground">{labels.dayDates[d]}</p>
          </div>
        ))}

        {slots.map((m) => (
          <SlotRow
            key={m}
            m={m}
            days={days}
            durationMin={durationMin}
            selected={selected}
            onToggle={onToggle}
            full={candidates.length >= max}
            friends={friends}
            labels={labels}
            freeCountLabel={freeCountLabel}
          />
        ))}
      </div>
    </div>
  )
}

function SlotRow({
  m,
  days,
  durationMin,
  selected,
  onToggle,
  full,
  friends,
  labels,
  freeCountLabel,
}: {
  m: number
  days: number[]
  durationMin: number
  selected: Set<string>
  onToggle: (c: Candidate) => void
  full: boolean
  friends: Friend[]
  labels: WeekLabels
  freeCountLabel: (dayLabel: string, timeLabel: string, count: number) => string
}) {
  const onHour = m % 60 === 0
  return (
    <>
      <div
        className={cn(
          'flex h-11 items-center justify-end pr-1 text-[11px] tabular-nums',
          onHour ? 'font-bold text-muted-foreground' : 'text-muted-foreground/60',
        )}
      >
        {fmt(m)}
      </div>
      {days.map((d) => {
        const key = `${d}-${m}`
        const isSel = selected.has(key)
        const free = freeFriends(friends, d, m, durationMin)
        const disabled = !isSel && full
        return (
          <button
            key={key}
            type="button"
            disabled={disabled}
            aria-pressed={isSel}
            aria-label={freeCountLabel(labels.dayNames[d] ?? '', fmt(m), free.length)}
            data-testid={`candidate-slot-${d}-${m}`}
            onClick={() => onToggle({ day: d, start: m })}
            className={cn(
              'relative flex h-11 items-center justify-between gap-1 overflow-hidden rounded-xl border px-1.5 text-left transition-colors',
              onHour ? 'border-border' : 'border-border/50',
              free.length >= 3 && !isSel && 'bg-free/70',
              free.length > 0 && free.length < 3 && !isSel && 'bg-maybe/50',
              free.length === 0 && !isSel && 'bg-background',
              isSel && 'border-primary bg-primary text-primary-foreground',
              disabled && 'opacity-40',
            )}
          >
            {isSel ? (
              <span className="flex min-w-0 items-center gap-1 font-display text-[11px] font-extrabold tabular-nums">
                <Check className="size-3.5 shrink-0" />
                <span className="truncate">
                  {fmt(m)}–{fmt(m + durationMin)}
                </span>
              </span>
            ) : (
              <span className="flex min-w-0 items-center -space-x-1.5">
                {free.slice(0, 3).map((f) => (
                  <AvatarDisc
                    key={f.id}
                    seed={f.id}
                    emoji={f.emoji}
                    imgUrl={f.imgUrl}
                    masked={f.masked}
                    size="xxs"
                    className="border border-card"
                  />
                ))}
                {free.length > 3 && (
                  <span className="grid size-5 shrink-0 place-items-center rounded-full border border-card bg-muted text-[9px] font-bold text-muted-foreground">
                    +{free.length - 3}
                  </span>
                )}
              </span>
            )}
          </button>
        )
      })}
    </>
  )
}
