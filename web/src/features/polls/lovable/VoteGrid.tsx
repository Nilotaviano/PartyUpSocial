import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { cn } from '../../availability/lovable/utils'
import { GridLines } from '../../availability/lovable/GridLayers'
import { createGridGeometry } from '../../availability/lovable/gridEditing'
import { ROW_H, SLOT_MIN, clamp, fmt } from '../../availability/lovable/partyup-data'
import { useIsMobile } from '../../availability/lovable/use-mobile'
import { VOTE_META, optionRange, type PollFormatters, type PollOptionT, type VoteValue } from './pollData'

/**
 * A tap-to-vote enhancement over the canonical `VotingGrid` radiogroup list
 * (`../VotingGrid.tsx`, unchanged and still the primary a11y path): every
 * option paints as its own band, positioned by the shared grid core
 * (`../../availability/lovable/gridEditing.ts`'s `createGridGeometry`,
 * `GridLayers.tsx`'s `GridLines`) at its real day/time/D-211 duration.
 *
 * Per the core author's handoff, this is a pure tap surface — no
 * `useGridGestures`, no move/resize/create. A band is a plain `<button>`
 * carrying its own click handler; there is nothing to distinguish from a
 * drag because nothing here ever drags.
 *
 * Unlike `CandidateGrid`/`WeekGrid`, a poll's options are not one navigable
 * week: they can land on any dates the organizer or a suggestion picked. The
 * day columns here are therefore built directly from the *distinct* days the
 * options actually occupy (sorted ascending), not from a Mon-Sun week grid —
 * simplest-correct per the ticket's own steer, and it can never overflow
 * horizontally because the column count is capped and paged exactly like
 * `CandidateGrid`'s mobile pager (`onPageStep` idiom, inlined here since this
 * component owns its own "week").
 */

const NEXT_VOTE: Record<'none' | VoteValue, VoteValue> = {
  none: 'available',
  available: 'maybe',
  maybe: 'unavailable',
  unavailable: 'available',
}

function nextVote(current: VoteValue | null): VoteValue {
  return NEXT_VOTE[current ?? 'none']
}

type DayColumn = {
  key: string
  date: Date
  options: PollOptionT[]
}

/** Desktop shows at most a week's worth of columns; mobile pages by 3 (CandidateGrid's window). */
const DESKTOP_WINDOW = 7
const MOBILE_WINDOW = 3

type Props = {
  options: PollOptionT[]
  disabled: boolean
  pendingOptionId: string | null
  selectedOptionId: string | null
  formatters: PollFormatters
  onVote: (optionId: string, choice: VoteValue) => void
}

export function VoteGrid({
  options,
  disabled,
  pendingOptionId,
  selectedOptionId,
  formatters,
  onVote,
}: Props) {
  const { t, i18n } = useTranslation('polls')
  const isMobile = useIsMobile()
  const [pageStart, setPageStart] = useState(0)
  const [liveMessage, setLiveMessage] = useState('')

  const dayNameFmt = useMemo(
    () => new Intl.DateTimeFormat(i18n.language, { weekday: 'short', timeZone: 'UTC' }),
    [i18n.language],
  )
  const dayDateFmt = useMemo(
    () => new Intl.DateTimeFormat(i18n.language, { day: 'numeric', month: 'short', timeZone: 'UTC' }),
    [i18n.language],
  )

  const days: DayColumn[] = useMemo(() => {
    const map = new Map<string, DayColumn>()
    for (const option of options) {
      const midnight = new Date(
        Date.UTC(option.start.getUTCFullYear(), option.start.getUTCMonth(), option.start.getUTCDate()),
      )
      const key = midnight.toISOString()
      const existing = map.get(key)
      if (existing) existing.options.push(option)
      else map.set(key, { key, date: midnight, options: [option] })
    }
    return [...map.values()].sort((a, b) => a.date.getTime() - b.date.getTime())
  }, [options])

  const windowSize = Math.min(isMobile ? MOBILE_WINDOW : DESKTOP_WINDOW, Math.max(days.length, 1))
  const maxPageStart = Math.max(0, days.length - windowSize)
  const clampedPageStart = clamp(pageStart, 0, maxPageStart)
  const visibleDays = days.slice(clampedPageStart, clampedPageStart + windowSize)
  const canPage = days.length > windowSize

  const { rangeStart, rangeEnd } = useMemo(() => {
    if (options.length === 0) return { rangeStart: 8 * 60, rangeEnd: 22 * 60 }
    let min = 24 * 60
    let max = 0
    for (const option of options) {
      const startMin = option.start.getUTCHours() * 60 + option.start.getUTCMinutes()
      const endMin = startMin + option.durationMinutes
      min = Math.min(min, startMin)
      max = Math.max(max, endMin)
    }
    const start = clamp(Math.floor(min / 60) * 60 - 60, 0, 23 * 60)
    const end = clamp(Math.ceil(max / 60) * 60 + 60, start + 60, 24 * 60)
    return { rangeStart: start, rangeEnd: end }
  }, [options])

  const geometry = useMemo(
    () => createGridGeometry({ rangeStart, rangeEnd, slotMin: SLOT_MIN, rowH: ROW_H }),
    [rangeStart, rangeEnd],
  )
  const { pixelHeight: height, top, span: hgt } = geometry

  if (options.length === 0) return null

  const hours: number[] = []
  for (let m = rangeStart; m <= rangeEnd; m += 60) hours.push(m)

  function stepDays(delta: -1 | 1) {
    setPageStart(clamp(clampedPageStart + delta, 0, maxPageStart))
  }

  const gridTemplate = `56px repeat(${visibleDays.length}, minmax(0,1fr))`

  return (
    <div className="surface p-3" data-testid="poll-vote-grid">
      <div className="flex flex-wrap items-center justify-between gap-2 pb-2">
        <div className="min-w-0">
          <h3 className="font-display text-base font-extrabold">{t('voteGrid.heading')}</h3>
          <p className="text-[12px] text-muted-foreground">{t('voteGrid.hint')}</p>
        </div>
        {canPage && (
          <div className="flex items-center gap-1.5" data-testid="vote-grid-pager">
            <button
              aria-label={t('voteGrid.earlierDays')}
              className="inline-flex size-9 items-center justify-center rounded-xl border border-border disabled:opacity-40"
              disabled={clampedPageStart === 0}
              type="button"
              onClick={() => stepDays(-1)}
            >
              <ChevronLeft className="size-4" />
            </button>
            <button
              aria-label={t('voteGrid.laterDays')}
              className="inline-flex size-9 items-center justify-center rounded-xl border border-border disabled:opacity-40"
              disabled={clampedPageStart >= maxPageStart}
              type="button"
              onClick={() => stepDays(1)}
            >
              <ChevronRight className="size-4" />
            </button>
          </div>
        )}
      </div>

      <div aria-hidden="true" className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }}>
        <div />
        {visibleDays.map((day) => (
          <div
            key={day.key}
            className="flex min-w-0 flex-col items-center rounded-xl border border-border bg-card px-1 py-1.5 text-center"
          >
            <span className="font-display text-[13px] leading-tight font-bold text-ink">
              {dayNameFmt.format(day.date)}
            </span>
            <span className="truncate text-[11px] text-ink-soft">{dayDateFmt.format(day.date)}</span>
          </div>
        ))}
      </div>

      <div className="relative mt-1">
        <div className="grid gap-1.5" style={{ gridTemplateColumns: gridTemplate }}>
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

          {visibleDays.map((day) => (
            <div
              key={day.key}
              className="relative overflow-hidden rounded-2xl border border-border bg-card"
              data-testid={`vote-grid-day-${day.key}`}
              style={{ height }}
            >
              <GridLines geometry={geometry} />
              {day.options.map((option) => {
                const startMin = option.start.getUTCHours() * 60 + option.start.getUTCMinutes()
                const endMin = Math.min(startMin + option.durationMinutes, rangeEnd)
                const isFinal = selectedOptionId === option.id
                const muted = Boolean(selectedOptionId) && !isFinal
                const meta = option.viewerVote ? VOTE_META[option.viewerVote] : null
                const isPending = pendingOptionId === option.id
                const timeText = optionRange(option, formatters)
                const valueText = option.viewerVote ? t(`vote.${option.viewerVote}`) : t('detail.noVoteYet')

                return (
                  <button
                    key={option.id}
                    aria-busy={isPending}
                    aria-label={t('voteGrid.bandLabel', { time: timeText, value: valueText })}
                    className={cn(
                      'absolute inset-x-1 z-20 overflow-hidden rounded-xl border-2 px-1.5 py-1 text-left transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus disabled:cursor-not-allowed disabled:opacity-50',
                      meta ? meta.cls : 'border-border bg-card text-ink-soft',
                      isFinal && 'ring-2 ring-inset ring-primary',
                      muted && 'opacity-60',
                    )}
                    data-testid={`vote-grid-band-${option.id}`}
                    disabled={disabled}
                    style={{ top: top(startMin), height: Math.max(hgt(startMin, endMin), 20) }}
                    type="button"
                    onClick={() => {
                      const next = nextVote(option.viewerVote)
                      onVote(option.id, next)
                      setLiveMessage(t('voteGrid.voteAnnounced', { time: timeText, value: t(`vote.${next}`) }))
                    }}
                  >
                    <span className="block truncate font-display text-[11px] font-bold tabular-nums">
                      {timeText}
                    </span>
                  </button>
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
